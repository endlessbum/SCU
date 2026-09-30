using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;
using SCU.Common;

namespace SCU.Infrastructure.Browser;

// Сведения о доступности WebView2 Runtime. IsMissing отличает «Runtime не
// установлен» (предлагаем установку) от ошибки проверки (установка не поможет).
public sealed record BrowserRuntimeInfo(bool IsAvailable, string Version, string Error, bool IsMissing);

// Контракт для тестов: BrowserViewModel не должен создавать WebView2 до нажатия
// «Запустить» — всё обращение к браузерной среде идёт через этот интерфейс.
public interface IBrowserService
{
    Task<BrowserRuntimeInfo> CheckRuntimeAsync();
    Task<bool> InstallRuntimeAsync(IProgress<string> progress, CancellationToken ct = default);
    Task InitializeEnvironmentAsync();
    CoreWebView2Environment? Environment { get; }
    string NewTabUrl { get; }
    void EnsureNewTabFile(bool darkTheme, string accentHex);
    void MapVirtualHost(CoreWebView2 core);
}

// Ленивая браузерная среда: CoreWebView2Environment создаётся один раз и только
// по явному вызову InitializeEnvironmentAsync (кнопка «Запустить»). UserDataFolder —
// отдельный профиль %LOCALAPPDATA%\SCU\Browser, sandbox WebView2 не отключается.
public sealed class BrowserService : IBrowserService
{
    // Официальный канал Microsoft: Evergreen Bootstrapper WebView2 Runtime.
    private const string RuntimeBootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const string VirtualHostName = "scu.example";

    private readonly Logger _logger;
    private CoreWebView2Environment? _environment;

    public BrowserService(Logger logger)
    {
        _logger = logger;
    }

    public CoreWebView2Environment? Environment => _environment;

    public string NewTabUrl => "https://" + VirtualHostName + "/newtab.html";

    // Проверка Runtime: чтение версии без создания браузерных процессов.
    public Task<BrowserRuntimeInfo> CheckRuntimeAsync()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            _logger.Info("BROWSER | runtime check | available | " + version);
            return Task.FromResult(new BrowserRuntimeInfo(true, version, string.Empty, IsMissing: false));
        }
        catch (WebView2RuntimeNotFoundException exception)
        {
            _logger.Warn("BROWSER | runtime check | missing | " + exception.Message);
            return Task.FromResult(new BrowserRuntimeInfo(false, string.Empty, exception.Message, IsMissing: true));
        }
        catch (Exception exception)
        {
            _logger.Error("BROWSER | runtime check | failed | " + exception);
            return Task.FromResult(new BrowserRuntimeInfo(false, string.Empty, exception.Message, IsMissing: false));
        }
    }

    // Установка Runtime официальным бутстраппером Microsoft (скачивание с go.microsoft.com).
    // Требует интернета; результат проверяется повторным CheckRuntimeAsync.
    public async Task<bool> InstallRuntimeAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        var setupPath = string.Empty;
        try
        {
            // Случайное имя в Temp: предсказуемый путь — гонка с локальной подменой файла.
            setupPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SCU_WebView2Setup_" + Guid.NewGuid().ToString("N") + ".exe");

            progress.Report(L.T("Скачивание WebView2 Runtime…"));
            using (var http = new HttpClient())
            {
                await using var stream = await http.GetStreamAsync(RuntimeBootstrapperUrl, ct).ConfigureAwait(true);
                await using var file = new FileStream(setupPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(file, ct).ConfigureAwait(true);
            }

            // Запуск исполняемого файла из сети — только после проверки подписи Microsoft
            // (тот же механизм, что и для Edge setup.exe в BloatService).
            var verify = SignatureVerifier.VerifyMicrosoftSigned(setupPath);
            if (!verify.IsSuccess)
            {
                _logger.Error("BROWSER | runtime install | signature check failed | " + verify.Message);
                progress.Report(L.T("Проверка подписи установщика не пройдена — установка отменена."));
                return false;
            }

            progress.Report(L.T("Установка WebView2 Runtime…"));

            // Защита от TOCTOU: reparse point, повторная проверка подписи и
            // read-lock до Process.Start — файл не подменить после проверки.
            using var launchLock = VerifiedLaunch.OpenLocked(setupPath, _logger);
            if (launchLock is null)
            {
                progress.Report(L.T("Проверка установщика перед запуском не пройдена — установка отменена."));
                return false;
            }

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "/silent /install",
                UseShellExecute = true,
            };
            process.Start();
            await process.WaitForExitAsync(ct).ConfigureAwait(true);
            _logger.Info("BROWSER | runtime install | rc=" + process.ExitCode);

            var check = await CheckRuntimeAsync().ConfigureAwait(true);
            return check.IsAvailable;
        }
        catch (Exception exception)
        {
            _logger.Error("BROWSER | runtime install failed | " + exception);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(setupPath))
                {
                    File.Delete(setupPath);
                }
            }
            catch (Exception exception)
            {
                _logger.Warn("BROWSER | runtime installer cleanup | " + exception.Message);
            }
        }
    }

    // Создание среды может зависнуть навсегда: известное ограничение WebView2
    // при запуске приложения с повышенными правами ДРУГОГО пользователя
    // (MicrosoftEdge/WebView2Feedback #4672, #932) и в неинтерактивных сессиях.
    // Таймаут обязателен: вместо вечного спиннера — понятная ошибка и повтор.
    private static readonly TimeSpan EnvironmentTimeout = TimeSpan.FromSeconds(30);

    // Создание среды: ровно один раз за запуск SCU.
    public Task InitializeEnvironmentAsync() => InitializeEnvironmentAsync(null);

    public async Task InitializeEnvironmentAsync(string? userDataFolderOverride)
    {
        if (_environment is not null)
        {
            return;
        }

        var userDataFolder = userDataFolderOverride ?? BrowserDataPaths.UserDataFolder;
        _logger.Info("BROWSER | environment create | " + userDataFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            // Язык интерфейса браузерных диалогов следует языку SCU.
            Language = L.Current == AppLanguage.Ru ? "ru-RU" : "en-US",
            // Поддержка расширений выключена на уровне среды: контролы её
            // не используют, а профиль — общий для всех вкладок SCU.
            AreBrowserExtensionsEnabled = false,
            // Защита от трекинга включена явно; уровень Strict задаёт вкладка.
            EnableTrackingPrevention = true,
        };

        var creation = CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: options);

        var completed = await Task.WhenAny(creation, Task.Delay(EnvironmentTimeout)).ConfigureAwait(true);
        if (completed != creation)
        {
            _logger.Error("BROWSER | environment create | timeout after " + EnvironmentTimeout.TotalSeconds + "s");
            throw new TimeoutException(L.T("Создание браузерной среды не завершилось за отведённое время."));
        }

        _environment = await creation.ConfigureAwait(true);
        _logger.Info("BROWSER | environment created");
    }

    // Локальная стартовая страница: пишется в профиль браузера и открывается через
    // virtual host (https://scu.example/newtab.html) — работает без интернета и
    // проходит политику навигации как обычный https.
    public void EnsureNewTabFile(bool darkTheme, string accentHex)
    {
        try
        {
            var folder = System.IO.Path.Combine(BrowserDataPaths.UserDataFolder, "NewTab");
            System.IO.Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, "newtab.html");
            File.WriteAllText(path, BuildNewTabHtml(darkTheme, accentHex));
        }
        catch (Exception exception)
        {
            _logger.Warn("BROWSER | new tab file failed | " + exception.Message);
        }
    }

    // Привязка virtual host → папка со стартовой страницей (вызывается для каждого WebView2).
    // DenyCrossOrigin: папка недоступна другим origin'ам — сайты не могут ни прочитать
    // её содержимое через fetch/iframe, ни fingerprint'ить наличие SCU.
    public void MapVirtualHost(CoreWebView2 core) =>
        core.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            System.IO.Path.Combine(BrowserDataPaths.UserDataFolder, "NewTab"),
            CoreWebView2HostResourceAccessKind.DenyCors);

    private static string BuildNewTabHtml(bool darkTheme, string accentHex)
    {
        // accentHex интерполируется в HTML доверенной страницы: контракт — только
        // hex-цвет; валидация защищает от инъекции, если источник цвета изменится.
        if (!System.Text.RegularExpressions.Regex.IsMatch(accentHex, "^#[0-9A-Fa-f]{6}$"))
        {
            accentHex = "#0A84FF";
        }

        var background = darkTheme ? "#202020" : "#F3F3F3";
        var card = darkTheme ? "#2B2B2B" : "#FFFFFF";
        var text = darkTheme ? "#FFFFFF" : "#1A1A1A";
        var muted = darkTheme ? "#9E9E9E" : "#5C5C5C";

        return $$"""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>SCU Browser</title>
<style>
  html, body { margin: 0; height: 100%; }
  body {
    background: {{background}}; color: {{text}};
    font-family: "Segoe UI", sans-serif;
    display: flex; align-items: center; justify-content: center;
  }
  .panel { width: min(520px, 80%); text-align: center; }
  .logo { font-size: 28px; font-weight: 600; margin-bottom: 32px; }
  .logo span { color: {{accentHex}}; }
  .search {
    width: 100%; box-sizing: border-box; padding: 12px 18px;
    font-size: 15px; border-radius: 10px; border: 1px solid {{muted}}44;
    background: {{card}}; color: {{text}}; outline: none;
  }
  .search:focus { border-color: {{accentHex}}; }
</style>
</head>
<body>
  <div class="panel">
    <div class="logo">SCU <span>Browser</span></div>
    <form action="https://www.google.com/search" method="get">
      <input class="search" type="text" name="q" placeholder="Поиск в Google" autofocus>
    </form>
  </div>
</body>
</html>
""";
    }
}

