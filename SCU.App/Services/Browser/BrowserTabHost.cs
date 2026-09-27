using System.Text.RegularExpressions;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SCU.Common;
using SCU.Models.Browser;
using SCU.ViewModels.Sections;

namespace SCU.Services.Browser;

// Один экземпляр — одна вкладка: создаёт WebView2, применяет политику безопасности
// и транслирует события движка в модель вкладки и ViewModel. Создаётся ТОЛЬКО по
// явному запросу (после «Запустить»); закрытие вкладки освобождает все ресурсы.
public sealed class BrowserTabHost : IDisposable
{
    private readonly BrowserTabModel _model;
    private readonly IBrowserService _browserService;
    private readonly BrowserViewModel _viewModel;
    private BrowserSettingsModel _settings;
    private readonly Logger _logger;

    // NavigationId текущей top-level навигации: отделяет её от фреймов и быстрых
    // переходов — история и статус пишутся только для целевой страницы.
    private ulong _topNavigationId = ulong.MaxValue;

    public BrowserTabHost(
        BrowserTabModel model,
        IBrowserService browserService,
        BrowserViewModel viewModel,
        BrowserSettingsModel settings,
        Logger logger)
    {
        _model = model;
        _browserService = browserService;
        _viewModel = viewModel;
        _settings = settings;
        _logger = logger;
    }

    public BrowserTabModel Model => _model;

    public WebView2 View { get; private set; } = null!;

    public void UpdateSettings(BrowserSettingsModel settings) => _settings = settings;

    // Фаза 1: создание контрола. Контрол ДОЛЖЕН быть добавлен в визуальное дерево
    // и видим ДО InitializeAsync: EnsureCoreWebView2Async откладывает создание
    // контроллера до появления HWND, иначе задача не завершается никогда.
    public WebView2 CreateView()
    {
        if (View is not null)
        {
            return View;
        }

        var view = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
        };
        View = view;
        view.ZoomFactor = _settings.DefaultZoom;
        view.KeyDown += OnViewKeyDown;
        return View;
    }

    // Фаза 2: инициализация CoreWebView2 и первичная навигация. Вызывается на
    // UI-потоке для контрола, уже добавленного в дерево. При неудаче контрол
    // освобождается — живой WebView2 не утекает.
    public async Task InitializeAsync(string startUrl)
    {
        var environment = _browserService.Environment
            ?? throw new InvalidOperationException("Browser environment is not initialized.");

        if (View is null)
        {
            CreateView();
        }

        try
        {
            _logger.Info("BROWSER | tab core init | id=" + _model.Id);
            await View.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

            var core = View.CoreWebView2;
            ApplySecuritySettings(core);
            _browserService.MapVirtualHost(core);

            core.NavigationStarting += OnNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.SourceChanged += OnSourceChanged;
            core.DocumentTitleChanged += OnDocumentTitleChanged;
            core.NewWindowRequested += OnNewWindowRequested;
            core.DownloadStarting += OnDownloadStarting;
            core.PermissionRequested += OnPermissionRequested;
            core.ProcessFailed += OnProcessFailed;

            _logger.Info("BROWSER | tab created | id=" + _model.Id);
            core.Navigate(startUrl);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Недоверенный контент: без host-объектов и web-message моста, без
    // автосохранения паролей/autofill, без devtools (в Release) и без браузерных
    // акселераторов (Ctrl+W и пр. обслуживает приложение). Sandbox — по умолчанию.
    private void ApplySecuritySettings(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
    }

    // ===================== События движка =====================

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var decision = BrowserNavigationPolicy.Evaluate(e.Uri);
        switch (decision)
        {
            case BrowserNavigationDecision.Allow:
                _model.IsLoading = true;
                _model.StatusText = string.Empty;
                _topNavigationId = e.NavigationId;
                _logger.Info("BROWSER | navigate | " + ShortUrl(e.Uri));
                return;

            case BrowserNavigationDecision.Block:
                e.Cancel = true;
                _logger.Warn("BROWSER | navigation blocked | " + ShortUrl(e.Uri));
                _model.StatusText = L.T("Навигация заблокирована: {0}", e.Uri);
                _viewModel.ReportBlockedNavigation();
                return;

            case BrowserNavigationDecision.ExternalWithConfirmation:
                e.Cancel = true;
                _logger.Info("BROWSER | external protocol | " + ShortUrl(e.Uri));
                _viewModel.LaunchExternalScheme(e.Uri);
                return;
        }
    }

    // Политика обязана действовать и внутри фреймов: iframe на разрешённой странице
    // не должен запускать внешние схемы и обходить блокировки. Без обновлений
    // IsLoading/истории — они относятся только к top-level.
    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var decision = BrowserNavigationPolicy.Evaluate(e.Uri);
        if (decision == BrowserNavigationDecision.Allow)
        {
            return;
        }

        e.Cancel = true;
        _logger.Warn("BROWSER | frame navigation blocked | " + ShortUrl(e.Uri));
        if (decision == BrowserNavigationDecision.ExternalWithConfirmation)
        {
            _viewModel.LaunchExternalScheme(e.Uri);
        }
        else
        {
            _viewModel.ReportBlockedNavigation();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _model.IsLoading = false;
        _viewModel.NotifyNavigationStateChanged();

        if (e.NavigationId != _topNavigationId)
        {
            return;
        }

        _topNavigationId = ulong.MaxValue;
        if (e.IsSuccess)
        {
            _model.StatusText = string.Empty;
            // История — только top-level http/https; about:blank и прочее не пишем.
            if (_settings.SaveHistory
                && Uri.TryCreate(_model.Url, UriKind.Absolute, out var source)
                && source.Scheme is "http" or "https")
            {
                _viewModel.RecordHistory(_model.Url, _model.Title);
            }
            return;
        }

        var status = e.WebErrorStatus;
        _model.StatusText = status switch
        {
            CoreWebView2WebErrorStatus.ConnectionAborted
                or CoreWebView2WebErrorStatus.ConnectionReset
                or CoreWebView2WebErrorStatus.Disconnected
                or CoreWebView2WebErrorStatus.CannotConnect
                or CoreWebView2WebErrorStatus.HostNameNotResolved
                or CoreWebView2WebErrorStatus.Timeout
                or CoreWebView2WebErrorStatus.ServerUnreachable => L.T("Не удалось подключиться к интернету."),
            CoreWebView2WebErrorStatus.CertificateIsInvalid
                or CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
                or CoreWebView2WebErrorStatus.CertificateExpired => L.T("Ошибка сертификата сайта."),
            _ => L.T("Страница недоступна."),
        };
        _logger.Warn($"BROWSER | navigation failed | {ShortUrl(_model.Url)} | {status}");
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        _model.Url = View.CoreWebView2.Source;
        _model.CanGoBack = View.CanGoBack;
        _model.CanGoForward = View.CanGoForward;
        // Адресная строка обновляется только для активной вкладки и только когда
        // пользователь не печатает — иначе ввод затирается фоновыми redirect'ами.
        _viewModel.OnTabSourceChanged(_model);
    }

    private void OnDocumentTitleChanged(object? sender, object e) =>
        _model.Title = string.IsNullOrWhiteSpace(View.CoreWebView2.DocumentTitle)
            ? L.T("Новая вкладка")
            : View.CoreWebView2.DocumentTitle;

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // target=_blank / window.open — внутренняя вкладка вместо отдельного окна.
        e.Handled = true;
        if (BrowserNavigationPolicy.Evaluate(e.Uri) == BrowserNavigationDecision.Allow)
        {
            _ = _viewModel.OpenTab(e.Uri);
        }
        else
        {
            _logger.Warn("BROWSER | popup blocked | " + ShortUrl(e.Uri));
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Камера, микрофон, геолокация и прочее — только явное пользовательское
        // решение; по умолчанию всё запрещено автоматически.
        e.Handled = true;
        e.State = CoreWebView2PermissionState.Deny;
        _logger.Warn("BROWSER | permission denied | " + e.PermissionKind + " | " + ShortUrl(e.Uri));
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.Error("BROWSER | process failed | " + e.ProcessFailedKind);
        if (e.ProcessFailedKind
            is not (CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.FrameRenderProcessExited))
        {
            return;
        }

        // Рендер упал — SCU жив; вкладка предлагает повторить загрузку.
        _model.IsLoading = false;
        _model.StatusText = L.T("Браузерная вкладка была перезапущена.");
    }

    // ===================== Загрузки =====================

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;
        var operation = e.DownloadOperation;

        // Имя файла: Content-Disposition сервера, иначе последний сегмент URL.
        var suggested = !string.IsNullOrWhiteSpace(operation.ContentDisposition)
            ? FileNameFromContentDisposition(operation.ContentDisposition) ?? SafeFileName(operation.Uri)
            : SafeFileName(operation.Uri);

        var path = BrowserDownloadPolicy.BuildDownloadPath(_settings.DownloadFolder, suggested);
        if (path is null)
        {
            _logger.Error("BROWSER | download path refused | " + suggested);
            _viewModel.ReportDownloadFailure(L.T("Загрузка не удалась: небезопасное имя файла."));
            operation.Cancel();
            return;
        }

        if (_settings.AskDownloadPath)
        {
            // Путь выбирает пользователь в системном диалоге — сознательное действие,
            // граница папки загрузок на него намеренно не распространяется.
            var chosen = _viewModel.PickDownloadPath(path);
            if (chosen is null)
            {
                operation.Cancel();
                return;
            }
            path = chosen;
        }

        // Путь назначения задаётся только через DownloadStartingEventArgs.
        e.ResultFilePath = path;

        _logger.Info("BROWSER | download start | " + Path.GetFileName(path));
        _viewModel.AddDownload(operation, path, suggested, operation.Uri);
    }

    // filename*=UTF-8''... имеет приоритет; затем filename="..." или filename=...;
    // значение всегда проходит BrowserDownloadPolicy.SanitizeFileName — заголовок
    // сервера не доверяется (path traversal, абсолютные пути и пр.).
    internal static string? FileNameFromContentDisposition(string header)
    {
        var starMatch = Regex.Match(header, @"filename\*=(?:[^']*''){1,3}(?<value>[^;]+)", RegexOptions.IgnoreCase);
        if (starMatch.Success)
        {
            var decoded = Uri.UnescapeDataString(starMatch.Groups["value"].Value.Trim().Trim('"'));
            return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
        }

        var plainMatch = Regex.Match(header, @"filename=(?<value>""[^""]*""|[^;]+)", RegexOptions.IgnoreCase);
        if (plainMatch.Success)
        {
            return plainMatch.Groups["value"].Value.Trim().Trim('"');
        }

        return null;
    }

    // Подходящее имя из URL загрузки как fallback (без Content-Disposition).
    private static string SafeFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var name = Uri.UnescapeDataString(
                uri.Segments.Length > 0 ? uri.Segments[^1] : string.Empty).TrimEnd('/');

            var candidate = BrowserDownloadPolicy.SanitizeFileName(name);
            if (!string.IsNullOrEmpty(candidate))
            {
                return candidate;
            }

            return uri.Host + ".bin";
        }
        catch (Exception)
        {
            return "download.bin";
        }
    }

    // В приватный лог — только scheme+host+путь без query: токены сессий и
    // содержимое поисковых запросов в общий журнал SCU не попадают.
    internal static string ShortUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Scheme + "://" + uri.Authority + uri.AbsolutePath;
        }
        catch (Exception)
        {
            return "<invalid>";
        }
    }

    private void OnViewKeyDown(object sender, KeyEventArgs e) =>
        _viewModel.HandleShortcut(e.Key, Keyboard.Modifiers, e);

    // ===================== Команды навигации =====================

    public void Navigate(string url) => View.CoreWebView2.Navigate(url);

    public void GoBack()
    {
        if (View.CanGoBack)
        {
            View.GoBack();
        }
    }

    public void GoForward()
    {
        if (View.CanGoForward)
        {
            View.GoForward();
        }
    }

    public void Reload() => View.Reload();

    public void Stop() => View.CoreWebView2.Stop();

    public void SetZoom(double factor)
    {
        factor = Math.Clamp(factor, 0.8, 2.0);
        View.ZoomFactor = factor;
    }

    public double ZoomFactor => View?.ZoomFactor ?? 1.0;

    // Очистка данных сайтов (cookies/кэш) по профилю этой вкладки — best-effort:
    // при закрытии SCU завершение не гарантировано, решение фиксируется в журнале.
    public async void ClearBrowsingData(CoreWebView2BrowsingDataKinds kinds)
    {
        try
        {
            await View.CoreWebView2.Profile.ClearBrowsingDataAsync(kinds).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Warn("BROWSER | clear browsing data | " + exception.Message);
        }
    }

    // Закрытие вкладки: отписка, остановка, освобождение WebView2.
    public void Dispose()
    {
        if (View is null)
        {
            return;
        }

        try
        {
            var core = View.CoreWebView2;
            if (core is not null)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.FrameNavigationStarting -= OnFrameNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.SourceChanged -= OnSourceChanged;
                core.DocumentTitleChanged -= OnDocumentTitleChanged;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.PermissionRequested -= OnPermissionRequested;
                core.ProcessFailed -= OnProcessFailed;
                core.Stop();
            }

            View.KeyDown -= OnViewKeyDown;
            View.Dispose();
        }
        catch (Exception exception)
        {
            _logger.Warn("BROWSER | tab dispose | " + exception.Message);
        }

        _logger.Info("BROWSER | tab closed | id=" + _model.Id);
    }
}
