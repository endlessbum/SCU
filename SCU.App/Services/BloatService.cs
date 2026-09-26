using System.Diagnostics;
using SCU.Common;
using SCU.Interop;

namespace SCU.Services;

// Один пункт раздела «Удаление мусорного ПО»: как его сканирует AppxScan и как удалять.
public sealed record BloatApp(
    string ScanKey,
    string Title,
    string RemoveNames,
    bool Irreversible = false,
    string[]? KillProcesses = null);

// Результат AppxScan: scan=1/0 + значения 0/1/unknown по ключам.
public sealed record AppxScanState(bool Scanned, IReadOnlyDictionary<string, string> Values)
{
    public bool IsPresent(string key) =>
        Values.TryGetValue(key, out var v) && v == "1";

    public string StateText(string key) =>
        !Scanned || !Values.TryGetValue(key, out var value) ? "неизвестно (скан не удался)"
        : value == "1" ? "установлено"
        : "отсутствует";
}

// Раздел 4 «Удаление мусорного ПО» — аналог :BloatMenu из Utilities.bat.
// Скан и удаление — только через SCU.ps1 (AppxScan / AppxRemove / VerifyExe);
// скрипт сам проверяет отсутствие пакетов после удаления (rc=0 = подтверждено).
public sealed class BloatService
{
    private readonly Logger _logger;
    private readonly SCURunner _runner;
    private readonly LongProcessRunner _processRunner;
    private readonly string _scanFilePath;

    public BloatService(Logger logger, SCURunner runner, LongProcessRunner processRunner)
    {
        _logger = logger;
        _runner = runner;
        _processRunner = processRunner;
        _scanFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU", "cache", "appx_scan.txt");
    }

    public IReadOnlyList<BloatApp> Apps { get; } =
    [
        new BloatApp("Cam", "Камера", "Microsoft.WindowsCamera"),
        new BloatApp("Dev", "Центр разработки", "Microsoft.Windows.DevHome"),
        new BloatApp("Hub", "Центр отзывов", "Microsoft.WindowsFeedbackHub"),
        new BloatApp("Copilot", "Copilot", "Copilot,Windows.Ai"),
        new BloatApp("Bing", "Поиск Microsoft Bing", "BingSearch"),
        new BloatApp("Clip", "Microsoft Clipchamp", "Clipchamp.Clipchamp"),
        new BloatApp("News", "Новости Microsoft", "Microsoft.BingNews"),
        new BloatApp("Teams", "Microsoft Teams", "MSTeams", KillProcesses: ["msteams.exe"]),
        new BloatApp("ToDo", "Microsoft To Do", "Microsoft.Todos", KillProcesses: ["Todo.exe"]),
        new BloatApp("Outlook", "Outlook (новый)", "Microsoft.OutlookForWindows", KillProcesses: ["olk.exe"]),
        new BloatApp("Power", "Power Automate", "Microsoft.PowerAutomateDesktop",
            KillProcesses: ["PowerAutomate.exe", "PAD.Console.Host.exe", "PAD.DesktopBehavior.exe"]),
        new BloatApp("Quick", "Быстрая помощь", "MicrosoftCorporationII.QuickAssist"),
        new BloatApp("Sol", "Косынка", "Microsoft.MicrosoftSolitaireCollection"),
        new BloatApp("Sound", "Звукозапись", "Microsoft.WindowsSoundRecorder"),
        new BloatApp("Sticky", "Записки", "Microsoft.MicrosoftStickyNotes"),
        new BloatApp("Store", "Microsoft Store", "Microsoft.WindowsStore,Microsoft.StorePurchaseApp,Microsoft.Services.Store.Engagement",
            Irreversible: true),
        new BloatApp("Xbox", "Xbox (все компоненты)",
            "Microsoft.XboxApp,Microsoft.GamingApp,Microsoft.XboxGamingOverlay,Microsoft.XboxGameOverlay,Microsoft.XboxIdentityProvider,Microsoft.XboxSpeechToTextOverlay,Microsoft.Xbox.TCUI",
            KillProcesses: ["XboxPcApp.exe", "GameBar.exe", "GameBarFTServer.exe", "GamingServices.exe"])
    ];

    // Скан: AppxScan пишет key=value в файл, BAT разбирает его так же.
    public async Task<Result<AppxScanState>> ScanAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_scanFilePath)!);

        // Старый файл удаляем ДО запуска скрипта: иначе при частичном сбое записи
        // прочитаем результаты прошлого скана и ложно «подтвердим» удаление.
        try
        {
            File.Delete(_scanFilePath);
        }
        catch (Exception exception)
        {
            _logger.Error("BLOAT | scan | cannot delete old scan file: " + exception.Message);
            return Result<AppxScanState>.Failure("Не удалось удалить старый файл скана: " + exception.Message);
        }

        var result = await _runner
            .RunAsync("AppxScan", new Dictionary<string, string?> { ["OutFile"] = _scanFilePath }, null, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result<AppxScanState>.Failure(result.Message, result.Code);
        }

        if (!File.Exists(_scanFilePath))
        {
            return Result<AppxScanState>.Failure(
                "Скрипт отрапортовал успех, но файл скана не создан.", result.Code);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadAllLines(_scanFilePath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                {
                    values[parts[0].Trim()] = parts[1].Trim();
                }
            }
        }
        catch (Exception exception)
        {
            return Result<AppxScanState>.Failure("Не удалось прочитать файл скана: " + exception.Message);
        }

        var scanned = values.TryGetValue("scan", out var scanValue) && scanValue == "1";
        return Result<AppxScanState>.Success(new AppxScanState(scanned, values));
    }

    // Удаление одного пункта: процессы, winget (для Copilot), AppxRemove — скрипт верифицирует.
    public async Task<Result> RemoveAsync(BloatApp app, CancellationToken ct = default)
    {
        KillProcesses(app.KillProcesses);

        // Copilot в BAT дополнительно удаляется через winget (результат игнорируется, как >nul 2>&1).
        if (app.ScanKey == "Copilot")
        {
            await RemoveViaWingetAsync("Microsoft 365 Copilot", "Copilot", ct).ConfigureAwait(false);
        }

        var result = await _runner
            .RunAsync("AppxRemove", new Dictionary<string, string?> { ["Names"] = app.RemoveNames }, null, ct)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Result.Success(app.Title + ": пакет отсутствует после проверки.")
            : Result.Failure($"{app.Title}: удаление не подтверждено повторным сканированием (код {result.Code}).", result.Code);
    }

    // Microsoft Store в BAT — отдельный проход по трём пакетам (RemoveStore).
    public Task<Result> RemoveStoreAsync(CancellationToken ct = default) =>
        RemoveAsync(Apps.First(a => a.ScanKey == "Store"), ct);

    // Xbox в BAT: удаление + повторная проверка. Скрипт AppxRemove сам проверяет отсутствие
    // запрошенных пакетов (rc=0), дополнительно сверять по скану нельзя: шаблон 'Xbox' ловит
    // системный Microsoft.XboxGameCallableUI, который удалить штатно невозможно.
    public async Task<Result> RemoveXboxAsync(CancellationToken ct = default)
    {
        var app = Apps.First(a => a.ScanKey == "Xbox");
        var remove = await RemoveAsync(app, ct).ConfigureAwait(false);
        return remove.IsSuccess
            ? Result.Success("Xbox: удаляемые пакеты отсутствуют (проверено скриптом). Системный компонент XboxGameCallableUI остаётся в системе.")
            : remove;
    }

    // Удаление Edge: setup.exe --uninstall (после VerifyExe), winget (игнорируется), проверка файлов.
    public async Task<Result> RemoveEdgeAsync(CancellationToken ct = default)
    {
        // Умышленно трогаем и msedgewebview2: без этого setup.exe не сможет обновить занятые
        // файлы. Побочный ущерб (перезапуск WebView2-приложений) окупается только здесь —
        // в других пунктах Apps процессы webview2 не убиваются.
        KillProcesses(["msedge.exe", "msedgewebview2.exe"]);

        var setupPath = FindEdgeSetup();
        if (setupPath is not null)
        {
            // Нативная проверка подписи вместо PS VerifyExe (код 6 в части сессий).
            var verify = SignatureVerifier.VerifyMicrosoftSigned(setupPath);
            if (!verify.IsSuccess)
            {
                return Result.Failure("Edge setup.exe не прошёл проверку подписи Microsoft — удаление отменено. " + verify.Message, verify.Code);
            }

            var uninstall = await _processRunner
                .RunAsync(setupPath, ["--uninstall", "--system-level", "--force-uninstall", "--verbose-logging"], null, ct)
                .ConfigureAwait(false);
            _logger.Info("BLOAT | edge setup uninstall | rc=" + uninstall.Code);
            if (!uninstall.IsSuccess && uninstall.Code != 3010)
            {
                return Result.Failure("Edge: setup.exe завершился с кодом " + uninstall.Code + ".", uninstall.Code);
            }
        }

        await RemoveViaWingetAsync("Microsoft Edge", "Edge", ct).ConfigureAwait(false);

        var edgeLeft = EdgeFilesLeft();
        var scan = await ScanAsync(ct).ConfigureAwait(false);
        var appxLeft = scan.IsSuccess && scan.Value is not null && scan.Value.Values
            .Any(kv => kv.Key != "scan" && kv.Value == "1" && kv.Key.StartsWith("Edge", StringComparison.OrdinalIgnoreCase));

        return edgeLeft == 0 && !appxLeft
            ? Result.Success("Edge: удаление подтверждено проверкой файлов и AppX.")
            : Result.Failure("Edge: после удаления остались файлы или пакеты; см. лог.");
    }

    // Массовое удаление (BloatAll): всё перечисленное, включая Store и Edge, с финальной сверкой скана.
    public async Task<Result> RemoveAllAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var app in Apps)
        {
            ct.ThrowIfCancellationRequested();
            var remove = app.ScanKey == "Xbox"
                ? await RemoveXboxAsync(ct).ConfigureAwait(false)
                : await RemoveAsync(app, ct).ConfigureAwait(false);
            if (!remove.IsSuccess)
            {
                failures.Add(remove.Message);
            }
        }

        var edge = await RemoveEdgeAsync(ct).ConfigureAwait(false);
        if (!edge.IsSuccess)
        {
            failures.Add(edge.Message);
        }

        var scan = await ScanAsync(ct).ConfigureAwait(false);
        if (!scan.IsSuccess || scan.Value is null || !scan.Value.Scanned)
        {
            failures.Add("повторный скан AppX не удался");
        }
        else
        {
            var left = scan.Value.Values
                .Where(kv => kv.Key != "scan" && kv.Value == "1")
                .Select(kv => kv.Key)
                .ToList();
            if (left.Count > 0)
            {
                failures.Add("остались пакеты: " + string.Join(", ", left));
            }
        }

        return failures.Count == 0
            ? Result.Success("AppX/Edge: повторная проверка подтверждает отсутствие.")
            : Result.Failure("Массовое удаление завершилось не полностью: " + string.Join("; ", failures));
    }

    // Edge отсутствует, если msedge.exe нет в Program Files / Program Files (x86).
    public bool IsEdgeInstalled() => EdgeFilesLeft() > 0;

    // winget лежит не в PATH, а в WindowsApps текущего пользователя — резолвим полный путь.
    // Пакет не найден / winget отсутствует — best-effort проход, ошибкой не считается.
    private async Task RemoveViaWingetAsync(string packageName, string label, CancellationToken ct)
    {
        var wingetPath = FindWinget();
        if (wingetPath is null)
        {
            _logger.Info("BLOAT | winget " + label + " | winget не найден, пропуск");
            return;
        }

        var result = await _processRunner
            .RunAsync(
                wingetPath,
                ["uninstall", "--name", packageName, "--silent", "--accept-source-agreements"],
                null,
                ct)
            .ConfigureAwait(false);
        _logger.Info("BLOAT | winget " + label + " | rc=" + result.Code);
    }

    private string? FindWinget()
    {
        if (_wingetPath is not null)
        {
            return File.Exists(_wingetPath) ? _wingetPath : null;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe")
        };

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(
            pathVariable
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Combine(directory, "winget.exe")));

        var resolved = candidates.FirstOrDefault(File.Exists);
        _wingetPath = resolved;
        return resolved;
    }

    private string? _wingetPath;

    private static int EdgeFilesLeft()
    {
        var count = 0;
        foreach (var baseDir in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            if (baseDir is null)
            {
                continue;
            }

            var msedge = Path.Combine(baseDir, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(msedge))
            {
                count++;
            }
        }

        return count;
    }

    private string? FindEdgeSetup()
    {
        foreach (var baseDir in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            if (baseDir is null)
            {
                continue;
            }

            var applicationDir = Path.Combine(baseDir, "Microsoft", "Edge", "Application");
            if (!Directory.Exists(applicationDir))
            {
                continue;
            }

            try
            {
                // «Новейший» setup.exe — по версии файла, а не лексикографически:
                // строковая сортировка ставит 99.0.x выше 133.0.x.
                var setup = Directory
                    .EnumerateFiles(applicationDir, "setup.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p =>
                    {
                        try
                        {
                            return Version.Parse(FileVersionInfo.GetVersionInfo(p).FileVersion ?? "0.0");
                        }
                        catch
                        {
                            return new Version(0, 0);
                        }
                    })
                    .FirstOrDefault();
                if (setup is not null)
                {
                    return setup;
                }
            }
            catch (Exception exception)
            {
                _logger.Warn("BLOAT | edge setup search | " + exception.Message);
            }
        }

        return null;
    }

    // Только процессы текущей сессии: без фильтра запуск от админа убивал бы
    // процессы других пользователей (как в UIService.RestartExplorer).
    private static void KillProcesses(string[]? names)
    {
        if (names is null)
        {
            return;
        }

        var currentSession = Process.GetCurrentProcess().SessionId;
        foreach (var name in names)
        {
            var shortName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name[..^4]
                : name;
            foreach (var process in Process.GetProcessesByName(shortName))
            {
                if (process.SessionId != currentSession)
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Процесс мог завершиться сам.
                }

                process.Dispose();
            }
        }
    }
}
