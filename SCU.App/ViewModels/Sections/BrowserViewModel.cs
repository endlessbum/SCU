using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using SCU.Common;
using SCU.Interop;
using SCU.Models.Browser;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Мост ViewModel → View: создание/подмена/закрытие WebView2-контролов вкладок.
// Реализуется BrowserView; ViewModel не знает про XAML и не держит контролы сама.
public interface IBrowserHostBridge
{
    Task<bool> InitializeAsync(BrowserViewModel viewModel, BrowserTabModel firstTab);
    Task CreateTabViewAsync(BrowserTabModel tab);
    void ActivateTabView(BrowserTabModel tab);
    void CloseTabView(BrowserTabModel tab);
    void FocusAddress();
}

// Раздел 22 «Браузер»: два состояния — карточка запуска (ENTRY) и браузер (BROWSER).
// Открытие раздела НЕ создаёт ни WebView2, ни окружение: только StartBrowserCommand
// после явного нажатия «Запустить» обращается к IBrowserService.
public partial class BrowserViewModel : ObservableObject, IDisposable
{
    // Разумный предел живых вкладок: память важнее возможности открыть их бесконечно.
    private const int MaxTabs = 8;

    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly IShellOpenService _shell;
    private readonly IFilePickerService _filePicker;
    private readonly IBrowserService _browserService;
    private readonly ScannerRunner _scanner;
    private readonly BrowserSettingsService _settingsService;
    private readonly BrowserHistoryService _historyService;
    private readonly BrowserBookmarkService _bookmarkService;
    private readonly BrowserDownloadsService _downloadsService;

    private readonly Dictionary<int, BrowserTabHost> _hosts = [];
    private readonly List<(string Url, string Title)> _closedTabs = [];
    private readonly Dictionary<CoreWebView2DownloadOperation, BrowserDownloadItem> _operations = [];
    private int _nextTabId;
    private bool _disposed;
    private bool _settingsSync;
    private DateTime _lastExternalPrompt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartBrowserCommand))]
    private bool _isStarting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartBrowserCommand))]
    private bool _isBrowserStarted;

    [ObservableProperty]
    private bool _isRuntimeMissing;

    [NotifyCanExecuteChangedFor(nameof(InstallRuntimeCommand))]
    [ObservableProperty]
    private bool _isInstallingRuntime;

    [ObservableProperty]
    private string _statusText = string.Empty;

    // Предупреждение об известном ограничении WebView2 (elevated чужой учётки).
    [ObservableProperty]
    private string _elevationHint = string.Empty;

    [ObservableProperty]
    private string _addressText = string.Empty;

    [ObservableProperty]
    private BrowserTabModel? _activeTab;

    [ObservableProperty]
    private bool _isDownloadsOpen;

    [ObservableProperty]
    private bool _isHistoryOpen;

    [ObservableProperty]
    private bool _isBookmarksOpen;

    [ObservableProperty]
    private bool _isSettingsOpen;

    // Пользователь печатает в адресной строке — фоновые SourceChanged не трогают ввод.
    [ObservableProperty]
    private bool _isEditingAddress;

    [ObservableProperty]
    private BrowserSettingsModel _settings;

    // Обёртки настроек для TwoWay-биндингов чекбоксов: локальный клик по CheckBox
    // подменяет IsChecked и убивает OneWay-биндинг — состояние держит ViewModel.
    [ObservableProperty] private bool _askDownloadPath;
    [ObservableProperty] private bool _scanDownloads;
    [ObservableProperty] private bool _saveHistory;
    [ObservableProperty] private bool _clearHistoryOnExit;
    [ObservableProperty] private bool _clearCookiesOnExit;
    [ObservableProperty] private bool _clearCacheOnExit;

    public IBrowserHostBridge? Host { get; set; }

    // Отдельное окно браузера создаётся лениво: фабрика назначается разделом 22
    // (BrowserView), вызывается при первом «Запустить».
    internal Func<IBrowserHostBridge>? HostFactory { get; set; }

    // Показ/активация окна браузера (слушает BrowserWindow).
    public event Action? ShowWindowRequested;

    // Доступ моста (BrowserView) к сервису и журналу: WebView2-контролы создаёт
    // BrowserTabHost, окружение — всё тот же IBrowserService.
    internal IBrowserService Service => _browserService;

    internal Logger Log => _logger;

    public ObservableCollection<BrowserTabModel> Tabs { get; } = [];

    public ObservableCollection<BrowserDownloadItem> Downloads { get; } = [];

    // Индикатор «Не защищено»: активная вкладка открыта по http:// — трафик не
    // шифруется и может быть подменён по пути. Стартовая страница (virtual host)
    // и https индикатора не показывают.
    public bool IsInsecureConnection =>
        ActiveTab is { } tab
        && !NewTabUrlOf(tab)
        && Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp;

    public string RuntimeVersionText =>
        IsRuntimeMissing ? L.T("WebView2 Runtime не найден") : L.T("WebView2 Runtime: {0}", _runtimeVersion);

    private string _runtimeVersion = string.Empty;

    public string ProfilePathText => BrowserDataPaths.UserDataFolder;

    public BrowserViewModel(
        Logger logger,
        IConfirmDialogService dialogs,
        IBrowserService browserService,
        BrowserSettingsService settingsService,
        BrowserHistoryService historyService,
        BrowserBookmarkService bookmarkService,
        BrowserDownloadsService downloadsService)
        : this(logger, dialogs, browserService, settingsService, historyService, bookmarkService,
               downloadsService, new ShellOpenService(), new FilePickerService())
    {
    }

    public BrowserViewModel(
        Logger logger,
        IConfirmDialogService dialogs,
        IBrowserService browserService,
        BrowserSettingsService settingsService,
        BrowserHistoryService historyService,
        BrowserBookmarkService bookmarkService,
        BrowserDownloadsService downloadsService,
        IShellOpenService shell,
        IFilePickerService filePicker)
    {
        _logger = logger;
        _dialogs = dialogs;
        _shell = shell;
        _filePicker = filePicker;
        _browserService = browserService;
        _scanner = new ScannerRunner(logger);
        _settingsService = settingsService;
        _historyService = historyService;
        _bookmarkService = bookmarkService;
        _downloadsService = downloadsService;
        _settings = settingsService.Load();
        SyncSettingWrappers(_settings);

        // Свежие записи сверху: записи хранятся в хронологическом порядке,
        // разворачиваем при восстановлении; новые добавляются Insert(0).
        foreach (var record in _downloadsService.Entries.Reverse().Take(50))
        {
            // Legacy-файлы без StateKey и прерванные незавершённые состояния — «Прервано».
            var key = record.StateKey ?? BrowserDownloadItem.StateAborted;
            var item = new BrowserDownloadItem(record.FileName, record.FilePath, key, record.Url);
            if (item.IsUnfinished)
            {
                // SCU был закрыт посреди загрузки/проверки — состояние финализируем.
                item.StateKey = BrowserDownloadItem.StateAborted;
            }
            Downloads.Add(item);
        }
    }

    private void SyncSettingWrappers(BrowserSettingsModel settings)
    {
        _settingsSync = true;
        AskDownloadPath = settings.AskDownloadPath;
        ScanDownloads = settings.ScanDownloads;
        SaveHistory = settings.SaveHistory;
        ClearHistoryOnExit = settings.ClearHistoryOnExit;
        ClearCookiesOnExit = settings.ClearCookiesOnExit;
        ClearCacheOnExit = settings.ClearCacheOnExit;
        _settingsSync = false;
    }

    public IReadOnlyList<BrowserHistoryItem> HistoryEntries => _historyService.Entries;

    public IReadOnlyList<BrowserBookmark> BookmarkEntries => _bookmarkService.Entries;

    private bool CanStart() => !IsStarting && !IsBrowserStarted;

    // ===================== Запуск браузера =====================

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartBrowserAsync()
    {
        // Guard не только CanExecute: повторный вызов команды не создаёт вторую сессию.
        if (IsBrowserStarted || IsStarting)
        {
            return;
        }

        IsStarting = true;
        IsRuntimeMissing = false;
        StatusText = L.T("Запуск браузера…");
        _logger.Info("BROWSER | start requested");

        try
        {
            var runtime = await _browserService.CheckRuntimeAsync().ConfigureAwait(true);
            _runtimeVersion = runtime.Version;
            OnPropertyChanged(nameof(RuntimeVersionText));

            if (!runtime.IsAvailable)
            {
                if (runtime.IsMissing)
                {
                    IsRuntimeMissing = true;
                    StatusText = L.T("Компонент браузера не установлен.");
                }
                else
                {
                    // Ошибка проверки — не «Runtime нет»: установка не поможет.
                    StatusText = L.T("Не удалось проверить WebView2 Runtime: {0}", runtime.Error);
                }
                return;
            }

            // Окно браузера создаётся лениво при первом запуске (фабрика назначена
            // карточкой раздела); повторные запуски переиспользуют тот же мост.
            Host ??= HostFactory?.Invoke();
            if (Host is null)
            {
                _logger.Error("BROWSER | start failed | host bridge is not attached");
                StatusText = L.T("Не удалось запустить браузер. Подробности в журнале.");
                return;
            }

            if (ElevationUserCheck.RunsAsDifferentUserThanInteractive())
            {
                ElevationHint = L.T(
                    "SCU запущен от имени другого пользователя. Известное ограничение WebView2: браузер может не запуститься — при зависании перезапустите SCU обычным способом.");
                _logger.Warn("BROWSER | foreign elevation detected | WebView2 creation may hang");
            }

            await _browserService.InitializeEnvironmentAsync().ConfigureAwait(true);

            _browserService.EnsureNewTabFile(
                ThemeManager.IsDarkTheme(ThemeManager.LoadThemeMode()),
                ToHex(ThemeManager.CurrentAccentColor));

            var firstTab = new BrowserTabModel(++_nextTabId, _browserService.NewTabUrl, L.T("Новая вкладка"));
            if (!await Host.InitializeAsync(this, firstTab).ConfigureAwait(true))
            {
                StatusText = L.T("Не удалось запустить браузер. Подробности в журнале.");
                return;
            }

            Tabs.Add(firstTab);
            firstTab.IsActive = true;
            ActiveTab = firstTab;
            AddressText = string.Empty;
            IsBrowserStarted = true;
            StatusText = string.Empty;
            _logger.Info("BROWSER | started");

            // Окно браузера разворачивается после успешной инициализации вкладки.
            ShowWindowRequested?.Invoke();
        }
        catch (TimeoutException exception)
        {
            ElevationHint = ElevationUserCheck.RunsAsDifferentUserThanInteractive()
                ? L.T(
                    "SCU запущен от имени другого пользователя. Известное ограничение WebView2: браузер может не запуститься — при зависании перезапустите SCU обычным способом.")
                : string.Empty;
            StatusText = L.T(
                "Не удалось запустить браузер: {0} Проверьте окружение и нажмите «Запустить» ещё раз.", exception.Message);
            _logger.Error("BROWSER | start failed | timeout | " + exception.Message);
        }
        catch (Exception exception)
        {
            StatusText = L.T("Не удалось запустить браузер: {0}", exception.Message);
            _logger.Error("BROWSER | start failed | " + exception);
        }
        finally
        {
            IsStarting = false;
        }
    }

    // Установка WebView2 Runtime официальным бутстраппером Microsoft; после
    // установки повторный запуск возможен без перезапуска SCU.
    [RelayCommand(CanExecute = nameof(CanInstallRuntime))]
    private async Task InstallRuntimeAsync()
    {
        IsInstallingRuntime = true;
        StatusText = L.T("Скачивание WebView2 Runtime…");
        try
        {
            var installed = await _browserService
                .InstallRuntimeAsync(new Progress<string>(text => StatusText = L.S(text)))
                .ConfigureAwait(true);
            if (installed)
            {
                IsRuntimeMissing = false;
                StatusText = L.T("WebView2 Runtime установлен. Нажмите «Запустить».");
                _logger.Info("BROWSER | runtime installed");
                AppNotificationCenter.Instance.Push(
                    L.T("Компонент установлен"),
                    L.T("WebView2 Runtime установлен — встроенный браузер готов к запуску."),
                    AppNotificationKind.Success);
            }
            else
            {
                StatusText = L.T("Установить WebView2 Runtime не удалось. Проверьте интернет и повторите.");
            }
        }
        finally
        {
            IsInstallingRuntime = false;
        }
    }

    private bool CanInstallRuntime() => !IsInstallingRuntime;

    private static string ToHex(System.Windows.Media.Color color) =>
        "#" + color.ToString()[3..];

    // ===================== Вкладки =====================

    [RelayCommand]
    private async Task NewTabAsync()
    {
        await OpenTabAsync(_browserService.NewTabUrl).ConfigureAwait(true);
        Host?.FocusAddress();
    }

    // Открытие ссылки во внутренней вкладке (в том числе из popup'ов сайтов).
    internal async Task OpenTab(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            _logger.Warn("BROWSER | popup with invalid url | " + url);
            StatusText = L.T("Не удалось открыть ссылку: недопустимый адрес.");
            return;
        }

        await OpenTabAsync(url).ConfigureAwait(true);
    }

    // Единственная точка создания вкладки: лимит и политика проверяются для всех
    // входов — Ctrl+T, popup'ы сайтов и восстановление закрытых вкладок.
    private async Task OpenTabAsync(string url)
    {
        if (Tabs.Count >= MaxTabs)
        {
            StatusText = L.T("Достигнут предел вкладок ({0}).", MaxTabs);
            return;
        }

        if (BrowserNavigationPolicy.Evaluate(url) != BrowserNavigationDecision.Allow)
        {
            _logger.Warn("BROWSER | tab open blocked | " + BrowserTabHost.ShortUrl(url));
            StatusText = L.T("Адрес заблокирован политикой безопасности.");
            return;
        }

        var tab = new BrowserTabModel(++_nextTabId, url, L.T("Новая вкладка"));
        try
        {
            if (Host is not null)
            {
                await Host.CreateTabViewAsync(tab).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("BROWSER | tab create failed | " + exception);
            StatusText = L.T("Не удалось открыть вкладку: {0}", exception.Message);
            return;
        }

        Tabs.Add(tab);
        await ActivateTabAsync(tab).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CloseTabAsync(BrowserTabModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            // Повторное закрытие (гонка с асинхронным созданием новой) — no-op,
            // иначе получим дубликаты в _closedTabs и лишние вкладки.
            return;
        }

        if (!string.Equals(tab.Url, _browserService.NewTabUrl, StringComparison.Ordinal))
        {
            _closedTabs.Add((tab.Url, tab.Title));
            if (_closedTabs.Count > 20)
            {
                _closedTabs.RemoveAt(0);
            }
        }

        if (Host is not null)
        {
            Host.CloseTabView(tab);
        }

        if (_hosts.Remove(tab.Id, out var host))
        {
            host.Dispose();
        }

        var wasActive = ReferenceEquals(ActiveTab, tab);
        Tabs.Remove(tab);
        if (wasActive)
        {
            ActiveTab = null;
            AddressText = string.Empty;
        }

        if (Tabs.Count == 0)
        {
            // Последнюю вкладку не закрываем в пустоту — открываем новую стартовую.
            await OpenTabAsync(_browserService.NewTabUrl).ConfigureAwait(true);
            return;
        }

        if (wasActive)
        {
            await ActivateTabAsync(Tabs[^1]).ConfigureAwait(true);
        }
    }

    // Ctrl+Shift+T: восстановление последней закрытой вкладки.
    [RelayCommand]
    private async Task ReopenClosedTabAsync()
    {
        if (_closedTabs.Count == 0)
        {
            return;
        }

        var (url, _) = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        await OpenTabAsync(url).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NextTabAsync() => await CycleTab(+1).ConfigureAwait(true);

    [RelayCommand]
    private async Task PreviousTabAsync() => await CycleTab(-1).ConfigureAwait(true);

    private async Task CycleTab(int step)
    {
        if (Tabs.Count < 2)
        {
            return;
        }

        var index = ActiveTab is { } active ? Tabs.IndexOf(active) : -1;
        if (index < 0)
        {
            // ActiveTab рассинхронизирован (не должен случаться после guard'а
            // CloseTab) — безопасный fallback: последняя вкладка.
            index = Tabs.Count - 1;
        }
        else
        {
            index = (index + step + Tabs.Count) % Tabs.Count;
        }

        await ActivateTabAsync(Tabs[index]).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ActivateTabAsync(BrowserTabModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        foreach (var existing in Tabs)
        {
            existing.IsActive = ReferenceEquals(existing, tab);
        }

        ActiveTab = tab;
        AddressText = NewTabUrlOf(tab) ? string.Empty : tab.Url;
        Host?.ActivateTabView(tab);
        NotifyNavigationStateChanged();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private bool NewTabUrlOf(BrowserTabModel tab) =>
        string.Equals(tab.Url, _browserService.NewTabUrl, StringComparison.Ordinal);

    // SourceChanged любой вкладки: адресная строка обновляется только для активной
    // вкладки и только пока пользователь не печатает в ней.
    internal void OnTabSourceChanged(BrowserTabModel tab)
    {
        // Индикатор небезопасного соединения следует за URL независимо от
        // редактирования адресной строки — он про страницу, а не про ввод.
        OnPropertyChanged(nameof(IsInsecureConnection));

        if (!ReferenceEquals(tab, ActiveTab) || IsEditingAddress)
        {
            return;
        }

        AddressText = NewTabUrlOf(tab) ? string.Empty : tab.Url;
    }

    internal void OnActiveTabUrlChanged() => OnTabSourceChanged(ActiveTab!);

    // Смена активной вкладки (включая закрытие → null) меняет индикатор.
    partial void OnActiveTabChanged(BrowserTabModel? value) =>
        OnPropertyChanged(nameof(IsInsecureConnection));

    // ===================== Адресная строка и навигация =====================

    [RelayCommand]
    private async Task NavigateAddressAsync()
    {
        var resolved = BrowserNavigationPolicy.ResolveAddress(AddressText);
        if (resolved.Length == 0)
        {
            return;
        }

        await NavigateAsync(resolved).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NavigateAsync(string? url)
    {
        if (url is null || ActiveTab is not { } tab || !_hosts.TryGetValue(tab.Id, out var host))
        {
            return;
        }

        // Политика — обязательный фильтр ВСЕХ входов в Navigate, а не только
        // событий движка: закладки и история хранятся в JSON, который можно
        // отредактировать вне SCU, и не должны становиться обходом блокировок.
        if (BrowserNavigationPolicy.Evaluate(url) != BrowserNavigationDecision.Allow)
        {
            _logger.Warn("BROWSER | navigate blocked | " + BrowserTabHost.ShortUrl(url));
            StatusText = L.T("Адрес заблокирован политикой безопасности.");
            return;
        }

        if (NewTabUrlOf(tab) || string.IsNullOrEmpty(tab.Url))
        {
            // Из стартовой страницы навигация переиспользует текущую вкладку.
            tab.Url = url;
        }

        host.Navigate(url);
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private bool CanGoBack() => ActiveTab?.CanGoBack == true;

    private bool CanGoForward() => ActiveTab?.CanGoForward == true;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => ActiveHost()?.GoBack();

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => ActiveHost()?.GoForward();

    [RelayCommand]
    private void Reload() => ActiveHost()?.Reload();

    [RelayCommand]
    private void Stop() => ActiveHost()?.Stop();

    [RelayCommand]
    private void AddressEscape() => OnActiveTabUrlChanged();

    private BrowserTabHost? ActiveHost() =>
        ActiveTab is { } tab && _hosts.TryGetValue(tab.Id, out var host) ? host : null;

    // Регистрация созданного вкладкой моста (вызывается BrowserView).
    internal void RegisterHost(BrowserTabModel tab, BrowserTabHost host)
    {
        if (_disposed)
        {
            // VM уже освобождена — вкладка не должна утечь.
            host.Dispose();
            return;
        }

        _hosts[tab.Id] = host;
    }

    // CanGoBack/CanGoForward живут на модели вкладки — команды обновляются
    // из BrowserTabHost.OnSourceChanged и при переключении вкладок.
    internal void NotifyNavigationStateChanged()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    // ===================== Горячие клавиши =====================

    // Единая точка обработки: работает и из WPF-дерева (адресная строка, тулбар),
    // и из KeyDown вкладки при фокусе в web-контенте.
    internal bool HandleShortcut(Key key, ModifierKeys modifiers, KeyEventArgs? args = null)
    {
        if (!IsBrowserStarted)
        {
            return false;
        }

        var control = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        var alt = (modifiers & ModifierKeys.Alt) != 0;
        var handled = true;

        if (control && key == Key.L)
        {
            Host?.FocusAddress();
        }
        else if (control && shift && key == Key.T)
        {
            _ = ReopenClosedTabCommand.ExecuteAsync(null);
        }
        else if (control && key == Key.T)
        {
            _ = NewTabCommand.ExecuteAsync(null);
        }
        else if (control && key == Key.W)
        {
            CloseTabCommand.Execute(ActiveTab);
        }
        else if (control && key == Key.Tab)
        {
            _ = (shift ? PreviousTabCommand : NextTabCommand).ExecuteAsync(null);
        }
        else if (key == Key.F5 || control && key == Key.R)
        {
            ReloadCommand.Execute(null);
        }
        else if (alt && key == Key.Left)
        {
            GoBackCommand.Execute(null);
        }
        else if (alt && key == Key.Right)
        {
            GoForwardCommand.Execute(null);
        }
        else if (control && key == Key.D)
        {
            ToggleBookmarkCommand.Execute(null);
        }
        else if (control && key == Key.H)
        {
            ToggleHistoryPanelCommand.Execute(null);
        }
        else if (control && key == Key.J)
        {
            ToggleDownloadsCommand.Execute(null);
        }
        else if (control && key is Key.Add or Key.OemPlus)
        {
            ZoomInCommand.Execute(null);
        }
        else if (control && key is Key.Subtract or Key.OemMinus)
        {
            ZoomOutCommand.Execute(null);
        }
        else if (control && key is Key.D0 or Key.NumPad0)
        {
            ZoomResetCommand.Execute(null);
        }
        else
        {
            handled = false;
        }

        if (handled)
        {
            if (args is not null)
            {
                args.Handled = true;
            }
            _logger.Info("BROWSER | shortcut | " + modifiers + "+" + key);
        }

        return handled;
    }

    // ===================== Масштаб =====================

    [RelayCommand]
    private void ZoomIn() => ShiftZoom(+0.1);

    [RelayCommand]
    private void ZoomOut() => ShiftZoom(-0.1);

    [RelayCommand]
    private void ZoomReset() => SetZoom(1.0);

    private void ShiftZoom(double delta) =>
        SetZoom(Math.Round((ActiveHost()?.ZoomFactor ?? 1.0) + delta, 2));

    private void SetZoom(double factor)
    {
        factor = Math.Clamp(factor, 0.8, 2.0);
        ActiveHost()?.SetZoom(factor);
        StatusText = L.T("Масштаб: {0}%", (int)Math.Round(factor * 100));
    }

    // ===================== Закладки =====================

    [RelayCommand]
    private void ToggleBookmark()
    {
        if (ActiveTab is not { } tab || NewTabUrlOf(tab))
        {
            return;
        }

        if (_bookmarkService.Contains(tab.Url))
        {
            _bookmarkService.Remove(tab.Url);
            OnPropertyChanged(nameof(BookmarkEntries));
            StatusText = L.T("Закладка удалена.");
        }
        else
        {
            _bookmarkService.AddOrUpdate(tab.Url, tab.Title);
            OnPropertyChanged(nameof(BookmarkEntries));
            StatusText = L.T("Закладка добавлена.");
        }
    }

    [RelayCommand]
    private async Task OpenBookmarkAsync(string? url) => await NavigateAsync(url).ConfigureAwait(true);

    [RelayCommand]
    private void RemoveBookmark(string? url)
    {
        if (url is not null)
        {
            _bookmarkService.Remove(url);
            OnPropertyChanged(nameof(BookmarkEntries));
        }
    }

    // ===================== История =====================

    internal void RecordHistory(string url, string title)
    {
        if (url.StartsWith(_browserService.NewTabUrl, StringComparison.Ordinal))
        {
            return;
        }

        _historyService.Add(url, title);
        OnPropertyChanged(nameof(HistoryEntries));
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _historyService.Clear();
        OnPropertyChanged(nameof(HistoryEntries));
        StatusText = L.T("История браузера очищена.");
    }

    // ===================== Загрузки =====================

    [RelayCommand]
    private void ToggleDownloads() => IsDownloadsOpen = !IsDownloadsOpen;

    [RelayCommand]
    private void ToggleHistoryPanel() => IsHistoryOpen = !IsHistoryOpen;

    [RelayCommand]
    private void ToggleBookmarksPanel() => IsBookmarksOpen = !IsBookmarksOpen;

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    internal void AddDownload(
        CoreWebView2DownloadOperation operation, string path, string fileName, string url)
    {
        var item = new BrowserDownloadItem(fileName, path, BrowserDownloadItem.StateDownloading, url)
        {
            TotalBytes = operation.TotalBytesToReceive is { } total && total > 0 ? (long)total : null,
            BytesReceived = operation.BytesReceived,
        };
        Downloads.Insert(0, item);
        _downloadsService.AddOrUpdate(item);
        _operations[operation] = item;
        IsDownloadsOpen = true;

        // Event-driven API WebView2: без таймеров и polling'а.
        operation.BytesReceivedChanged += (_, _) =>
        {
            item.BytesReceived = operation.BytesReceived;
            item.TotalBytes = operation.TotalBytesToReceive is { } total && total > 0 ? (long)total : null;
        };
        operation.StateChanged += (_, _) => OnDownloadStateChanged(item, operation);
    }

    [RelayCommand]
    private void CancelDownload(BrowserDownloadItem? item)
    {
        var operation = _operations.FirstOrDefault(pair => ReferenceEquals(pair.Value, item)).Key;
        if (operation is not null)
        {
            // Дальше StateChanged выставит «Отменено»; ручной вызов не нужен:
            // состояние операции ещё InProgress.
            operation.Cancel();
        }
    }

    private void OnDownloadStateChanged(BrowserDownloadItem item, CoreWebView2DownloadOperation operation)
    {
        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                item.StateKey = BrowserDownloadItem.StateCompleted;
                _operations.Remove(operation);
                _logger.Info("BROWSER | download completed | " + Path.GetFileName(item.FilePath));
                if (Downloads.Contains(item))
                {
                    _downloadsService.AddOrUpdate(item);
                    StatusText = L.T("Файл скачан: {0}. Он не был запущен автоматически.", item.FileName);
                    AppNotificationCenter.Instance.Push(
                        L.T("Загрузка завершена"),
                        L.T("{0} — файл не был запущен автоматически.", item.FileName),
                        AppNotificationKind.Success);
                    ScanDownloadIfEnabled(item);
                }
                break;

            case CoreWebView2DownloadState.Interrupted:
                if (operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled)
                {
                    item.StateKey = BrowserDownloadItem.StateCanceled;
                }
                else
                {
                    item.StateKey = BrowserDownloadItem.StateError;
                    StatusText = L.T("Загрузка не удалась: {0}.", item.FileName);
                    _logger.Warn("BROWSER | download interrupted | " + operation.InterruptReason);
                }
                _operations.Remove(operation);
                if (Downloads.Contains(item))
                {
                    _downloadsService.AddOrUpdate(item);
                }
                break;
        }
    }

    internal void ReportDownloadFailure(string message)
    {
        StatusText = message;
    }

    internal void ReportBlockedNavigation()
    {
        StatusText = L.T("Навигация заблокирована политикой безопасности.");
    }

    [RelayCommand]
    private void RemoveDownload(BrowserDownloadItem? item)
    {
        if (item is null)
        {
            return;
        }

        // Активную загрузку останавливаем: иначе StateChanged при Completed
        // «воскресит» запись в хранилище после удаления из списка.
        var operation = _operations.FirstOrDefault(pair => ReferenceEquals(pair.Value, item)).Key;
        if (operation is not null)
        {
            _operations.Remove(operation);
            operation.Cancel();
        }

        // Из списка: сам файл на диске не трогаем.
        Downloads.Remove(item);
        _downloadsService.Remove(item.FilePath);
    }

    [RelayCommand]
    private async Task RetryDownloadAsync(BrowserDownloadItem? item)
    {
        if (item is not null)
        {
            await OpenTab(item.Url).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenDownloadFolder(BrowserDownloadItem? item)
    {
        var folder = item is not null ? Path.GetDirectoryName(item.FilePath) : Settings.DownloadFolder;
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            if (item is not null && File.Exists(item.FilePath))
            {
                _shell.ShowInExplorer(item.FilePath);
            }
            else
            {
                _shell.OpenFolder(folder);
            }
        }
        catch (Exception exception)
        {
            _logger.Warn("BROWSER | open folder failed | " + exception.Message);
        }
    }

    [RelayCommand]
    private void OpenDownloadFile(BrowserDownloadItem? item)
    {
        if (item is null || !File.Exists(item.FilePath))
        {
            return;
        }

        // Скачанные из интернета файлы НЕ запускаются автоматически никогда:
        // открытие — только после явного подтверждения для любого файла,
        // для исполняемых — с отдельным предупреждением.
        var isExecutable = BrowserDownloadPolicy.IsExecutable(item.FilePath);
        var confirmed = _dialogs.Ask(
            L.T("Открыть файл"),
            isExecutable
                ? L.T("«{0}» — исполняемый файл, скачанный из интернета.\nЗапустить его? SCU не проверял файл автоматически.",
                    item.FileName)
                : L.T("Открыть файл «{0}», скачанный из интернета?", item.FileName),
            L.T("Открыть"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            _shell.OpenPath(item.FilePath);
        }
        catch (Exception exception)
        {
            StatusText = L.T("Не удалось открыть файл: {0}", exception.Message);
            _logger.Warn("BROWSER | open file failed | " + exception.Message);
        }
    }

    // Проверка скачанного файла ScannerCore (если включена в настройках).
    private async void ScanDownloadIfEnabled(BrowserDownloadItem item)
    {
        if (!Settings.ScanDownloads)
        {
            return;
        }

        if (!_scanner.IsAvailable)
        {
            item.ScanResult = L.T("встроенная проверка недоступна");
            _logger.Warn("BROWSER | download scan skipped | ScannerCore unavailable");
            return;
        }

        item.StateKey = BrowserDownloadItem.StateChecking;
        try
        {
            var result = await _scanner.RunScanAsync(item.FilePath, scanDirectory: false).ConfigureAwait(true);
            var detections = result.IsSuccess ? result.Value?.Detections.Count ?? 0 : 0;
            if (!result.IsSuccess)
            {
                item.ScanResult = L.T("ошибка проверки");
                _logger.Warn("BROWSER | download scan failed | " + result.Message);
                item.StateKey = BrowserDownloadItem.StateCompleted;
                return;
            }

            item.ScanResult = detections > 0
                ? L.T("обнаружено угроз: {0}", detections)
                : L.T("угроз не обнаружено");
            if (detections > 0)
            {
                item.StateKey = BrowserDownloadItem.StateThreat;
                StatusText = L.T("В скачанном файле {0} обнаружены угрозы. Файл не запускался.", item.FileName);
                AppNotificationCenter.Instance.Push(
                    L.T("Сканер: угрозы в загрузке"),
                    L.T("В файле {0} обнаружены угрозы ({1}). Файл не запускался.", item.FileName, detections),
                    AppNotificationKind.Danger);
            }
            else
            {
                // Проверка закончена — файл остаётся «Завершено», не «Проверяется».
                item.StateKey = BrowserDownloadItem.StateCompleted;
            }
            _logger.Info($"BROWSER | download scan | {Path.GetFileName(item.FilePath)} | detections={detections}");
        }
        catch (Exception exception)
        {
            item.ScanResult = L.T("ошибка проверки");
            item.StateKey = BrowserDownloadItem.StateCompleted;
            _logger.Error("BROWSER | download scan error | " + exception);
        }
    }

    // ===================== Внешние схемы и пути =====================

    // mailto:/tel:/ms-settings:/неизвестные схемы — только по явному подтверждению.
    internal void LaunchExternalScheme(string url)
    {
        // Повторная валидация: запускать разрешено только схемам с подтверждением.
        if (BrowserNavigationPolicy.Evaluate(url) != BrowserNavigationDecision.ExternalWithConfirmation)
        {
            _logger.Warn("BROWSER | external launch refused by policy | " + BrowserTabHost.ShortUrl(url));
            return;
        }

        // JS-цикл вызовов не должен долбить диалогами: чаще раза в секунду не спрашиваем.
        var now = DateTime.Now;
        if ((now - _lastExternalPrompt).TotalMilliseconds < 500)
        {
            _logger.Warn("BROWSER | external launch rate-limited | " + BrowserTabHost.ShortUrl(url));
            StatusText = L.T("Сайт запрашивает открытие внешнего приложения — подтверждение уже показано.");
            return;
        }
        _lastExternalPrompt = now;

        if (!_dialogs.Ask(
                L.T("Внешнее приложение"),
                L.T("Сайт запрашивает открытие внешнего приложения.\n\n{0}\n\nРазрешить?", url),
                L.T("Открыть")))
        {
            _logger.Info("BROWSER | external launch declined | " + BrowserTabHost.ShortUrl(url));
            return;
        }

        try
        {
            _shell.OpenPath(url);
            _logger.Info("BROWSER | external launch | " + BrowserTabHost.ShortUrl(url));
        }
        catch (Exception exception)
        {
            StatusText = L.T("Не удалось открыть внешнее приложение: {0}", exception.Message);
            _logger.Warn("BROWSER | external launch failed | " + exception.Message);
        }
    }

    // Диалог «куда сохранить» — на UI-потоке, из BrowserTabHost.
    internal string? PickDownloadPath(string suggestedPath)
    {
        return _filePicker.PickSaveFile(Path.GetFileName(suggestedPath), Path.GetDirectoryName(suggestedPath));
    }

    // ===================== Настройки =====================

    public void SaveSettings(BrowserSettingsModel settings)
    {
        _settingsSync = true;
        Settings = settings;
        SyncSettingWrappers(settings);
        _settingsSync = false;

        foreach (var host in _hosts.Values)
        {
            host.UpdateSettings(settings);
            host.SetZoom(settings.DefaultZoom);
        }

        _settingsService.Save(settings);
        _logger.Info("BROWSER | settings saved");
    }

    private void UpdateSettingIfChanged(Func<BrowserSettingsModel, BrowserSettingsModel> withValue)
    {
        if (_settingsSync)
        {
            return;
        }

        SaveSettings(withValue(Settings));
    }

    partial void OnAskDownloadPathChanged(bool value) =>
        UpdateSettingIfChanged(s => s with { AskDownloadPath = value });

    partial void OnScanDownloadsChanged(bool value) =>
        UpdateSettingIfChanged(s => s with { ScanDownloads = value });

    partial void OnSaveHistoryChanged(bool value) =>
        UpdateSettingIfChanged(s => s with { SaveHistory = value });

    partial void OnClearHistoryOnExitChanged(bool value) =>
        UpdateSettingIfChanged(s => s with { ClearHistoryOnExit = value });

    partial void OnClearCookiesOnExitChanged(bool value) =>
        UpdateSettingIfChanged(s => s with { ClearCookiesOnExit = value });

    partial void OnClearCacheOnExitChanged(bool value) =>
        UpdateSettingIfChanged(s => s with { ClearCacheOnExit = value });

    [RelayCommand]
    private void SaveDownloadFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        SaveSettings(Settings with { DownloadFolder = folder.Trim() });
    }

    [RelayCommand]
    private void ClearBrowsingDataNow()
    {
        _historyService.Clear();
        OnPropertyChanged(nameof(HistoryEntries));
        ClearWebViewDataAsync(
            CoreWebView2BrowsingDataKinds.Cookies
            | CoreWebView2BrowsingDataKinds.DiskCache);
        StatusText = L.T("История и данные сайтов очищены.");
    }

    // Очистка данных WebView2 — по профилю активной вкладки, best-effort.
    private void ClearWebViewDataAsync(CoreWebView2BrowsingDataKinds kinds)
    {
        try
        {
            ActiveHost()?.ClearBrowsingData(kinds);
        }
        catch (Exception exception)
        {
            _logger.Warn("BROWSER | clear data failed | " + exception.Message);
        }
    }

    // ===================== Окно браузера =====================

    // «Показать окно» из карточки раздела, когда браузер уже запущен.
    [RelayCommand]
    private void ShowBrowserWindow() => ShowWindowRequested?.Invoke();

    // Закрытие окна браузера = конец сессии: раздел возвращается к карточке
    // запуска, повторный «Запустить» открывает окно заново.
    internal void CloseBrowser()
    {
        if (!IsBrowserStarted && _hosts.Count == 0)
        {
            return;
        }

        _logger.Info("BROWSER | window closed");
        ShutdownSession();

        IsBrowserStarted = false;
        // Кнопка «Запустить» обязана вернуться в активное состояние сразу.
        StartBrowserCommand.NotifyCanExecuteChanged();
        ActiveTab = null;
        AddressText = string.Empty;
        Tabs.Clear();
        IsDownloadsOpen = IsHistoryOpen = IsBookmarksOpen = IsSettingsOpen = false;
        StatusText = string.Empty;
        Host = null;
    }

    private void ShutdownSession()
    {
        foreach (var host in _hosts.Values)
        {
            host.Dispose();
        }

        _hosts.Clear();
    }

    // ===================== Завершение работы =====================

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Settings.ClearHistoryOnExit)
            {
                _historyService.Clear();
            }

            if (Settings.ClearCookiesOnExit || Settings.ClearCacheOnExit)
            {
                var kinds = (CoreWebView2BrowsingDataKinds)0
                    | (Settings.ClearCookiesOnExit ? CoreWebView2BrowsingDataKinds.Cookies : 0)
                    | (Settings.ClearCacheOnExit ? CoreWebView2BrowsingDataKinds.DiskCache : 0);
                ClearWebViewDataAsync(kinds);
            }
        }
        catch (Exception exception)
        {
            _logger.Warn("BROWSER | dispose cleanup failed | " + exception.Message);
        }

        ShutdownSession();
    }
}
