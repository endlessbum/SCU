using SCU.Common;
using SCU.Infrastructure.Browser;
using SCU.Interop;
using SCU.Infrastructure.Windows.Drivers;
using SCU.Models.Browser;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// Регрессии по аудиту (п. 8.7): lifecycle браузера, gate WU-поиска,
// persisted-метка загрузок. Тесты не обращаются к реальному WebView2/COM.
public sealed class BrowserLifecycleAuditTests : IDisposable
{
    private readonly string _directory;
    private readonly Logger _logger;

    public BrowserLifecycleAuditTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logger = Logger.CreateForCurrentRun();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична.
        }
    }

    private BrowserViewModel CreateViewModel(BrowserSettingsModel? settings = null)
    {
        var vm = new BrowserViewModel(
            _logger,
            new ConfirmDialogServiceStub(),
            new FakeBrowserService(),
            new BrowserSettingsService(_logger, Path.Combine(_directory, "settings.json")),
            new BrowserHistoryService(_logger, Path.Combine(_directory, "history.json")),
            new BrowserBookmarkService(_logger, Path.Combine(_directory, "bookmarks.json")),
            new BrowserDownloadsService(_logger, Path.Combine(_directory, "downloads.json")),
            // ScannerCore в тестовом прогоне недоступен — авто-скан загрузок
            // корректно сообщит «встроенная проверка недоступна».
            new ScannerViewModel(
                _logger,
                new ScannerRunner(_logger),
                new HistoryStore(_logger, Path.Combine(_directory, "scanner-history.json"))));
        if (settings is not null)
        {
            vm.SaveSettings(settings);
        }
        return vm;
    }

    private BrowserSettingsModel Settings() =>
        BrowserSettingsModel.CreateDefault() with { DownloadFolder = Path.Combine(_directory, "downloads") };

    private sealed class ConfirmDialogServiceStub : IConfirmDialogService
    {
        public bool Ask(string title, string message, string? confirmText = null) => false;
    }

    private sealed class FakeBrowserService : IBrowserService
    {
        public Microsoft.Web.WebView2.Core.CoreWebView2Environment? Environment => null;

        public string NewTabUrl => "https://scu.example/newtab.html";

        public Task<BrowserRuntimeInfo> CheckRuntimeAsync() =>
            Task.FromResult(new BrowserRuntimeInfo(true, "140.0.0.0", string.Empty, IsMissing: false));

        public Task<bool> InstallRuntimeAsync(IProgress<string> progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task InitializeEnvironmentAsync() => Task.CompletedTask;

        public void EnsureNewTabFile(bool darkTheme, string accentHex)
        {
        }

        public void MapVirtualHost(Microsoft.Web.WebView2.Core.CoreWebView2 core) =>
            throw new NotSupportedException("not used in tests");
    }

    // Мост с хуком: внутри InitializeAsync можно выполнить действие — эмуляция
    // «окно закрыли посреди запуска» (H-03).
    private sealed class HookedBridge : IBrowserHostBridge
    {
        public Action<BrowserViewModel>? OnInitialize { get; set; }

        public Task<bool> InitializeAsync(BrowserViewModel viewModel, BrowserTabModel firstTab)
        {
            OnInitialize?.Invoke(viewModel);
            return Task.FromResult(true);
        }

        public Task CreateTabViewAsync(BrowserTabModel tab) => Task.CompletedTask;

        public void ActivateTabView(BrowserTabModel tab)
        {
        }

        public void CloseTabView(BrowserTabModel tab)
        {
        }

        public void FocusAddress()
        {
        }
    }

    // ===================== H-01: единственная задача среды =====================

    [Fact]
    public async Task EnvironmentInit_TimeoutThenRetry_ReusesSameCreateTask()
    {
        var service = new BrowserService(_logger)
        {
            EnvironmentTimeoutForTests = TimeSpan.FromMilliseconds(100),
        };
        var gate = new TaskCompletionSource<Microsoft.Web.WebView2.Core.CoreWebView2Environment>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        service.CreateEnvironmentOverride = _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return gate.Task;
        };

        // Таймаут ожидания не отменяет CreateAsync: задача продолжает выполняться.
        await Assert.ThrowsAsync<TimeoutException>(() => service.InitializeEnvironmentAsync());
        Assert.Equal(1, factoryCalls);
        gate.SetResult(null!);

        // Повторный запуск ждёт ТУ ЖЕ задачу: второй CreateAsync не создаётся.
        await service.InitializeEnvironmentAsync();

        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task EnvironmentInit_ConcurrentCalls_CreateEnvironmentOnce()
    {
        var service = new BrowserService(_logger);
        var gate = new TaskCompletionSource<Microsoft.Web.WebView2.Core.CoreWebView2Environment>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        service.CreateEnvironmentOverride = _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return gate.Task;
        };

        var first = service.InitializeEnvironmentAsync();
        var second = service.InitializeEnvironmentAsync();
        gate.SetResult(null!);
        await Task.WhenAll(first, second);

        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task EnvironmentInit_FaultedCreate_AllowsRetryWithNewTask()
    {
        var service = new BrowserService(_logger);
        var factoryCalls = 0;
        service.CreateEnvironmentOverride = _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromException<Microsoft.Web.WebView2.Core.CoreWebView2Environment>(
                new InvalidOperationException("boom"));
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeEnvironmentAsync());
        // Состоявшийся сбой не блокирует повтор навсегда: создаётся новая попытка.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeEnvironmentAsync());

        Assert.Equal(2, factoryCalls);
    }

    // ===================== H-03: закрытие окна во время запуска =====================

    [Fact]
    public async Task StartBrowser_WindowClosedDuringStart_DoesNotReportStarted()
    {
        var vm = CreateViewModel(Settings());
        var bridge = new HookedBridge
        {
            // Эмуляция: пользователь закрыл окно, пока шла инициализация вкладки.
            OnInitialize = candidate => candidate.CloseBrowser(),
        };
        vm.Host = bridge;
        var generationBefore = vm.SessionGeneration;

        await vm.StartBrowserCommand.ExecuteAsync(null);

        Assert.False(vm.IsBrowserStarted);
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.ActiveTab);
        Assert.Equal(generationBefore + 1, vm.SessionGeneration);
    }

    [Fact]
    public void CloseBrowser_WhenNeverStarted_DoesNotInvalidateSession()
    {
        var vm = CreateViewModel();
        var generationBefore = vm.SessionGeneration;

        vm.CloseBrowser();

        Assert.Equal(generationBefore, vm.SessionGeneration);
        Assert.False(vm.IsBrowserStarted);
    }

    [Fact]
    public async Task CloseBrowser_TwiceAfterStart_SessionGenerationAdvancesOncePerClose()
    {
        var vm = CreateViewModel(Settings());
        vm.Host = new HookedBridge();
        await vm.StartBrowserCommand.ExecuteAsync(null);
        Assert.True(vm.IsBrowserStarted);

        vm.CloseBrowser();
        var afterFirst = vm.SessionGeneration;
        vm.CloseBrowser();

        Assert.False(vm.IsBrowserStarted);
        Assert.Equal(afterFirst, vm.SessionGeneration);
    }

    // ===================== H-02: очистка данных при закрытии сессии =====================

    [Fact]
    public async Task CloseBrowser_WithClearOnExitSettings_CompletesWithoutDeadlock()
    {
        var vm = CreateViewModel(Settings() with { ClearCookiesOnExit = true, ClearCacheOnExit = true });
        vm.Host = new HookedBridge();
        await vm.StartBrowserCommand.ExecuteAsync(null);

        // Хостов нет (fake-мост не регистрирует вкладки) — очистка best-effort,
        // путь закрытия не должен ни падать, ни зависать.
        vm.CloseBrowser();

        Assert.False(vm.IsBrowserStarted);
    }

    // Аудит lifecycle (P1): CloseBrowserAsync дожидается очистки данных сайтов
    // зарегистрированного хоста и утилизирует хосты строго после неё. Точный
    // аналог реального сценария: ClearBrowsingDataAsync завершается позже.
    [Fact]
    public async Task CloseBrowserAsync_WithRegisteredHost_AwaitsCleanupBeforeDispose()
    {
        var vm = CreateViewModel(Settings() with { ClearCookiesOnExit = true, ClearCacheOnExit = true });
        vm.Host = new HookedBridge();
        await vm.StartBrowserCommand.ExecuteAsync(null);
        Assert.True(vm.IsBrowserStarted);

        var cleanupTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        var host = new CleanupFakeHost(_logger, vm, cleanupTcs.Task, log);
        vm.RegisterHost(new BrowserTabModel(1, "https://scu.example/newtab.html", "Fake"), host);

        var closeTask = vm.CloseBrowserAsync();

        // Очистка ещё не завершена — закрытие не должно «проехать» мимо неё.
        await Task.Delay(50);
        Assert.False(closeTask.IsCompleted);
        Assert.False(host.Disposed);

        cleanupTcs.TrySetResult(true);
        await closeTask;

        Assert.True(host.Disposed);
        // Порядок: сначала очистка, потом утилизация хостов.
        Assert.Equal(["cleanup", "dispose"], log);
        Assert.False(vm.IsBrowserStarted);
        Assert.Empty(vm.Tabs);
    }

    // Таймаут-вариант: хост, чья очистка не завершается, не подвешивает закрытие
    // (лимит 5 секунд из прежнего контракта сохранён).
    [Fact]
    public async Task CloseBrowserAsync_CleanupTimeout_CompletesAndDisposes()
    {
        var vm = CreateViewModel(Settings() with { ClearCookiesOnExit = true, ClearCacheOnExit = true });
        vm.Host = new HookedBridge();
        await vm.StartBrowserCommand.ExecuteAsync(null);

        var neverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new CleanupFakeHost(_logger, vm, neverCompletes.Task, []);
        vm.RegisterHost(new BrowserTabModel(1, "https://scu.example/newtab.html", "Fake"), host);

        await vm.CloseBrowserAsync();

        Assert.True(host.Disposed);
        Assert.False(vm.IsBrowserStarted);
    }

    // Fake-хост на шве виртуальных ClearBrowsingDataAsync/Dispose: конструктор
    // BrowserTabHost лёгкий (WebView2 не создаётся — CreateView не вызывается).
    private sealed class CleanupFakeHost(
        Logger logger, BrowserViewModel viewModel, Task<bool> cleanup, List<string> log)
        : BrowserTabHost(
            new BrowserTabModel(1, "https://scu.example/newtab.html", "Fake"),
            new FakeBrowserService(), viewModel, BrowserSettingsModel.CreateDefault(), logger)
    {
        public bool Disposed { get; private set; }

        public override async Task<bool> ClearBrowsingDataAsync(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds kinds)
        {
            log.Add("cleanup");
            return await cleanup.ConfigureAwait(false);
        }

        public override void Dispose()
        {
            Disposed = true;
            log.Add("dispose");
        }
    }

    // ===================== M-01: gate поискового COM-вызова =====================

    [Fact]
    public async Task WuSearch_WhilePreviousComSearchAlive_ReturnsBusy()
    {
        var service = new WindowsUpdateDriverService(_logger);
        // Живой abandoned COM-вызов после таймаута: новый поиск не должен стартовать.
        service._comSearchInFlight = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        var result = await service.SearchAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains("ещё выполняется", result.Message, StringComparison.Ordinal);
    }

    // ===================== L-01: persisted timestamp загрузок =====================

    [Fact]
    public void DownloadItem_RestoreTimestamp_SurvivesConstruction()
    {
        var persisted = new DateTime(2026, 1, 15, 12, 30, 0);

        var item = new BrowserDownloadItem("file.exe", @"C:\t\file.exe", BrowserDownloadItem.StateCompleted, "https://x", persisted);

        Assert.Equal(persisted, item.Timestamp);
    }

    [Fact]
    public void DownloadItem_WithoutTimestamp_UsesNow()
    {
        var before = DateTime.Now.AddSeconds(-1);

        var item = new BrowserDownloadItem("file.exe", @"C:\t\file.exe", BrowserDownloadItem.StateCompleted, "https://x");

        Assert.InRange(item.Timestamp, before, DateTime.Now.AddSeconds(1));
    }

    // ===================== L-02: атомарное резервирование имени =====================

    [Fact]
    public void ReserveDownloadPath_ReservesNameAtomically()
    {
        var folder = Path.Combine(_directory, "reserve");
        Directory.CreateDirectory(folder);
        var candidate = Path.Combine(folder, "file.bin");

        var reserved = BrowserDownloadPolicy.ReserveDownloadPath(candidate);

        Assert.Equal(candidate, reserved);
        Assert.True(File.Exists(candidate));
        Assert.Equal(0, new FileInfo(candidate).Length);
    }

    [Fact]
    public void ReserveDownloadPath_TakenName_FallsToNextCandidate()
    {
        var folder = Path.Combine(_directory, "reserve2");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "file.bin"), "occupied");
        var candidate = Path.Combine(folder, "file.bin");

        var reserved = BrowserDownloadPolicy.ReserveDownloadPath(candidate);

        Assert.Equal(Path.Combine(folder, "file (1).bin"), reserved);
        Assert.True(File.Exists(reserved));
    }
}
