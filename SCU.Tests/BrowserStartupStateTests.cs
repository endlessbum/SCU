using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Models.Browser;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// КРИТИЧЕСКОЕ: открытие раздела «Браузер» ≠ запуск браузера. Ни создание
// BrowserViewModel, ни команды навигации по вкладкам не должны обращаться к
// браузерной среде — только StartBrowserCommand инициирует IBrowserService.
public sealed class BrowserStartupStateTests : IDisposable
{
    private readonly string _directory;
    private readonly Logger _logger;
    private readonly FakeBrowserService _service;
    private readonly FakeBridge _bridge;

    public BrowserStartupStateTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logger = Logger.CreateForCurrentRun();
        _service = new FakeBrowserService { RuntimeAvailable = true };
        _bridge = new FakeBridge();
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

    private BrowserViewModel CreateViewModel() => new(
        _logger,
        new ConfirmDialogServiceStub(),
        _service,
        new BrowserSettingsService(_logger, Path.Combine(_directory, "settings.json")),
        new BrowserHistoryService(_logger, Path.Combine(_directory, "history.json")),
        new BrowserBookmarkService(_logger, Path.Combine(_directory, "bookmarks.json")),
        new BrowserDownloadsService(_logger, Path.Combine(_directory, "downloads.json")));

    private sealed class ConfirmDialogServiceStub : IConfirmDialogService
    {
        public bool Ask(string title, string message, string? confirmText = null) => false;
    }

    private sealed class FakeBrowserService : IBrowserService
    {
        public int RuntimeChecks { get; private set; }
        public int EnvironmentInitializations { get; private set; }
        public int NewTabFiles { get; private set; }

        public bool RuntimeAvailable { get; set; }

        public Microsoft.Web.WebView2.Core.CoreWebView2Environment? Environment => null;

        public string NewTabUrl => "https://scu.example/newtab.html";

        public Task<BrowserRuntimeInfo> CheckRuntimeAsync()
        {
            RuntimeChecks++;
            return Task.FromResult(RuntimeAvailable
                ? new BrowserRuntimeInfo(true, "140.0.0.0", string.Empty, IsMissing: false)
                : new BrowserRuntimeInfo(false, string.Empty, "missing", IsMissing: true));
        }

        public Task<bool> InstallRuntimeAsync(IProgress<string> progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task InitializeEnvironmentAsync()
        {
            EnvironmentInitializations++;
            return Task.CompletedTask;
        }

        public void EnsureNewTabFile(bool darkTheme, string accentHex) => NewTabFiles++;

        public void MapVirtualHost(Microsoft.Web.WebView2.Core.CoreWebView2 core) =>
            throw new NotSupportedException("not used in tests");
    }

    private sealed class FakeBridge : IBrowserHostBridge
    {
        public int Initializations { get; private set; }

        public Task<bool> InitializeAsync(BrowserViewModel viewModel, BrowserTabModel firstTab)
        {
            Initializations++;
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

    [Fact]
    public void Constructor_DoesNotTouchBrowserService()
    {
        _ = CreateViewModel();

        Assert.Equal(0, _service.RuntimeChecks);
        Assert.Equal(0, _service.EnvironmentInitializations);
    }

    [Fact]
    public void Constructor_IsNotStarted()
    {
        var vm = CreateViewModel();

        Assert.False(vm.IsBrowserStarted);
        Assert.False(vm.IsStarting);
        Assert.False(vm.IsRuntimeMissing);
        Assert.Empty(vm.Tabs);
    }

    [Fact]
    public async Task StartBrowser_WithMissingRuntime_ShowsRuntimeMissingState()
    {
        var vm = CreateViewModel();
        _service.RuntimeAvailable = false;

        await vm.StartBrowserCommand.ExecuteAsync(null);

        Assert.Equal(1, _service.RuntimeChecks);
        Assert.Equal(0, _service.EnvironmentInitializations);
        Assert.True(vm.IsRuntimeMissing);
        Assert.False(vm.IsBrowserStarted);
    }

    [Fact]
    public async Task StartBrowser_OnlyStartCommandInitializesEnvironment()
    {
        var vm = CreateViewModel();
        vm.Host = _bridge;

        await vm.StartBrowserCommand.ExecuteAsync(null);

        Assert.Equal(1, _service.RuntimeChecks);
        Assert.Equal(1, _service.EnvironmentInitializations);
        Assert.Equal(1, _service.NewTabFiles);
        Assert.True(vm.IsBrowserStarted);
        Assert.False(vm.IsRuntimeMissing);
        Assert.Single(vm.Tabs);
        Assert.True(vm.Tabs[0].IsActive);
    }

    [Fact]
    public async Task StartBrowser_Twice_DoesNotInitializeTwice()
    {
        var vm = CreateViewModel();
        vm.Host = _bridge;

        await vm.StartBrowserCommand.ExecuteAsync(null);
        await vm.StartBrowserCommand.ExecuteAsync(null);

        Assert.Equal(1, _bridge.Initializations);
        Assert.Single(vm.Tabs);
    }

    [Fact]
    public async Task TabOperations_DontReinitializeEnvironment()
    {
        var vm = CreateViewModel();
        vm.Host = _bridge;
        await vm.StartBrowserCommand.ExecuteAsync(null);

        await vm.NewTabCommand.ExecuteAsync(null);

        Assert.Equal(1, _service.EnvironmentInitializations);
        Assert.Equal(2, vm.Tabs.Count);
    }

    [Fact]
    public void Dispose_NeverTouchesBrowserService()
    {
        var vm = CreateViewModel();

        vm.Dispose();

        Assert.Equal(0, _service.EnvironmentInitializations);
    }
}
