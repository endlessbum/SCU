using System.Diagnostics;
using Microsoft.Win32;
using SCU.Common;

namespace SCU.Services;

// Установленное приложение из ARP-раздела реестра (Add/Remove Programs).
// Показывается всё, у чего есть DisplayName, включая скрытые SystemComponent
// записи — вкладка «Приложения» задумана как полный список программ системы.
public sealed class InstalledApp
{
    public string DisplayName { get; init; } = string.Empty;
    public string? DisplayVersion { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString);

    public string Subtitle
    {
        get
        {
            var parts = new[] { Publisher, DisplayVersion }.Where(p => !string.IsNullOrWhiteSpace(p));
            var text = string.Join(" • ", parts);
            return text.Length > 0 ? text : "—";
        }
    }
}

// Чтение и запуск удаления установленных программ. Источник — ветки Uninstall-
// реестра: HKLM (64 и 32 бита) и HKCU (64 и 32 бита). Удаление запускает
// родной деинсталлятор приложения — так же, как «Установка и удаление программ».
public sealed class InstalledAppsService
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Wow64UninstallRoot = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    // (корень, путь ветки, представление реестра). WOW6432Node открывается в
    // 64-битном представлении по явному пути — так видны обе ветки на x64-системах.
    private static readonly (RegistryHive Hive, string Path, RegistryView View)[] Sources =
    [
        (RegistryHive.LocalMachine, UninstallRoot, RegistryView.Registry64),
        (RegistryHive.LocalMachine, Wow64UninstallRoot, RegistryView.Registry64),
        (RegistryHive.CurrentUser, UninstallRoot, RegistryView.Default),
        (RegistryHive.CurrentUser, Wow64UninstallRoot, RegistryView.Default)
    ];

    private readonly Logger _logger;

    public InstalledAppsService(Logger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.Ordinal);

        foreach (var (root, path, view) in Sources)
        {
            ReadBranch(apps, root, path, view);
        }

        return apps.Values
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ReadBranch(
        Dictionary<string, InstalledApp> apps,
        RegistryHive hive,
        string path,
        RegistryView view)
    {
        try
        {
            using var branch = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
            if (branch is null)
            {
                return;
            }

            foreach (var subKeyName in branch.GetSubKeyNames())
            {
                try
                {
                    using var subKey = branch.OpenSubKey(subKeyName);
                    var displayName = subKey?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    var entry = new InstalledApp
                    {
                        DisplayName = displayName.Trim(),
                        DisplayVersion = (subKey!.GetValue("DisplayVersion") as string)?.Trim(),
                        Publisher = (subKey.GetValue("Publisher") as string)?.Trim(),
                        InstallLocation = (subKey.GetValue("InstallLocation") as string)?.Trim(),
                        UninstallString = NormalizeUninstallString(
                            (subKey.GetValue("QuietUninstallString") as string)?.Trim()
                            ?? (subKey.GetValue("UninstallString") as string)?.Trim())
                    };

                    // Одна программа видна в 64- и 32-битной ветках — дубль убираем
                    // по имени, версии и команде удаления.
                    apps.TryAdd($"{entry.DisplayName}|{entry.DisplayVersion}|{entry.UninstallString}", entry);
                }
                catch (Exception exception)
                {
                    _logger.Warn($"APPS | entry {subKeyName} | {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Warn($"APPS | read {hive}\\{path} | {exception.Message}");
        }
    }

    // MSI-записи вида «MsiExec.exe /I{GUID}» переключаются на /X: иначе вместо
    // удаления открывается диалог «изменить установку».
    private static string? NormalizeUninstallString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith("MsiExec.exe /I", StringComparison.OrdinalIgnoreCase))
        {
            return "MsiExec.exe /X" + value["MsiExec.exe /I".Length..];
        }

        return value;
    }

    // Запуск родного деинсталлятора. Ожидание завершения не выполняется:
    // установщики могут перезапускаться с повышением прав или уходить в фон.
    public Result StartUninstall(InstalledApp app)
    {
        if (!app.HasUninstaller)
        {
            return Result.Failure("У приложения нет программы удаления.", 1);
        }

        var (fileName, arguments) = SplitCommandLine(app.UninstallString!);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Result.Failure("Не удалось разобрать команду удаления.", 1);
        }

        try
        {
            // Дочерний процесс деинсталлятора живёт независимо; using закрывает
            // только хендл родителя, не процесс.
            using (Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            }))
            {
            }

            _logger.Info($"APPS | uninstall started | {app.DisplayName}");
            return Result.Success("Запущено удаление.");
        }
        catch (Exception exception)
        {
            _logger.Error($"APPS | uninstall failed | {app.DisplayName} | {exception.Message}");
            return Result.Failure("Не удалось запустить удаление: " + exception.Message, 1);
        }
    }

    // Разбор командной строки деинсталлятора: путь может быть в кавычках или без них.
    private static (string FileName, string Arguments) SplitCommandLine(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 0
                ? (trimmed[1..closing], trimmed[(closing + 1)..].TrimStart())
                : (trimmed.Trim('"'), string.Empty);
        }

        var exeMarker = trimmed.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
        if (exeMarker > 0)
        {
            return (trimmed[..(exeMarker + 4)], trimmed[(exeMarker + 5)..].TrimStart());
        }

        return (trimmed, string.Empty);
    }

    // ===================== Принудительное удаление с зачисткой =====================

    // Таймаут ожидания тихого деинсталлятора, после которого дерево процессов снимается.
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(10);

    // Полная зачистка: тихий деинсталлятор -> (по таймауту) снятие дерева
    // процессов -> остатки. Выполняется в фоне (долгая операция), токен —
    // от «Отмены» раздела.
    //
    // confirmDataCleanup — отдельное явное подтверждение удаления папок данных,
    // найденных по имени приложения (п. 6 аудита: имя владельцем не является).
    // null / false / отмена — папки данных не удаляются; папка установки
    // удаляется только при подтверждённом ownership (деинсталлятор живёт в ней).
    public Task<Result> UninstallCompletelyAsync(
        InstalledApp app,
        CancellationToken ct,
        Func<IReadOnlyList<string>, Task<bool>>? confirmDataCleanup = null) =>
        Task.Run(() => UninstallCompletely(app, ct, confirmDataCleanup), CancellationToken.None);

    private Result UninstallCompletely(
        InstalledApp app,
        CancellationToken ct,
        Func<IReadOnlyList<string>, Task<bool>>? confirmDataCleanup)
    {
        if (!app.HasUninstaller)
        {
            return Result.Failure("У приложения нет программы удаления.", 1);
        }

        var (fileName, arguments) = SplitCommandLine(app.UninstallString!);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Result.Failure("Не удалось разобрать команду удаления.", 1);
        }

        // Graceful первым (п. 6 аудита): штатный деинсталлятор сам корректно
        // закрывает приложение. Процессы снимаются принудительно только если
        // деинсталлятор не уложился в таймаут.
        arguments = AppendSilentFlags(fileName, arguments, app.InstallLocation);

        var killed = 0;
        var exited = true;
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                // ShellExecute, а не CreateProcess: деинсталляторы часто требуют
                // повышения (манифест requireAdministrator) — CreateProcess в этом
                // случае падает с ошибкой 740, ShellExecute поднимает права штатно.
                UseShellExecute = true
            };
            if (!process.Start())
            {
                return Result.Failure("Система не запустила деинсталлятор.", 1);
            }

            // Тихие деинсталляторы завершаются сами; не завершился за таймаут —
            // снимаем процессы приложения и дерево деинсталлятора.
            if (!process.WaitForExit((int)UninstallTimeout.TotalMilliseconds))
            {
                exited = false;
                killed = KillAppProcesses(app);
                _logger.Warn($"APPS | uninstall timed out | processes killed={killed} | " + app.DisplayName);
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        _logger.Warn("APPS | uninstall tree killed | " + app.DisplayName);
                    }
                }
                catch (Exception killException)
                {
                    _logger.Warn("APPS | uninstall kill failed | " + killException.Message);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Error($"APPS | uninstall failed | {app.DisplayName} | {exception.Message}");
            return Result.Failure("Не удалось запустить удаление: " + exception.Message, 1);
        }
        ct.ThrowIfCancellationRequested();

        // Хвосты: папка установки (только при подтверждённом ownership) и папки
        // данных, совпадающие по имени приложения — после явного подтверждения.
        var removed = RemoveResidualFolders(app, fileName, confirmDataCleanup, out var failedFolders, out var skippedDataFolders);

        var summary = $"Удаление «{app.DisplayName}» завершено"
            + (exited ? "." : " (деинсталлятор снят по таймауту).")
            + $" Процессов остановлено: {killed}. Папок зачищено: {removed}.";
        if (skippedDataFolders.Count > 0)
        {
            summary += " Папки данных не удалялись: " + string.Join("; ", skippedDataFolders) + ".";
        }
        if (failedFolders.Count > 0)
        {
            summary += " Не удалось удалить: " + string.Join("; ", failedFolders) + ".";
        }

        _logger.Info($"APPS | uninstall complete | {app.DisplayName} | killed={killed} folders={removed} failed={failedFolders.Count}");
        return Result.Success(summary);
    }

    // Тихие ключи по семейству установщика: MSI, Inno Setup, NSIS.
    private static string AppendSilentFlags(string fileName, string arguments, string? installLocation)
    {
        var exe = Path.GetFileName(fileName).ToLowerInvariant();
        if (exe is "msiexec.exe")
        {
            return arguments + " /qn /norestart";
        }

        if (exe.StartsWith("unins") && exe.Contains("000"))
        {
            // Inno Setup: unins000.exe/unins001.exe…
            return arguments + " /VERYSILENT /NORESTART /SUPPRESSMSGBOXES";
        }

        if (exe.Contains("uninstall"))
        {
            // NSIS и близкие: /S — тихий режим. NSIS-деинсталлятор копирует себя
            // в %TEMP% и родительский процесс мгновенно завершается, поэтому без
            // _?= ожидание фиктивно. _?=<папка установки> запускает его на месте
            // (сама папка деинсталлятор при этом не удаляет — за ней идёт зачистка).
            var installDirectory = NormalizeDirectory(installLocation);
            return arguments + " /S" + (installDirectory is { } location ? " _?=" + location : string.Empty);
        }

        return arguments;
    }

    // Процессы приложения в текущей сессии: исполняемые файлы из папки установки
    // и сам деинсталлятор, если уже запущен. Путь недоступен — процесс пропускается.
    private int KillAppProcesses(InstalledApp app)
    {
        var installLocation = NormalizeDirectory(app.InstallLocation);
        var uninstallerName = Path.GetFileName(
            SplitCommandLine(app.UninstallString ?? string.Empty).FileName);
        if (string.Equals(uninstallerName, "msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            uninstallerName = string.Empty; // системный процесс трогать нельзя
        }

        var currentSession = Process.GetCurrentProcess().SessionId;
        var killed = 0;
        foreach (var process in Process.GetProcesses())
        {
            if (process.SessionId != currentSession)
            {
                process.Dispose();
                continue;
            }

            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // Системные/защищённые процессы: путь недоступен — не наши.
            }

            var isApp = path is not null
                && (IsUnderDirectory(path, installLocation)
                    || (uninstallerName.Length > 0
                        && string.Equals(Path.GetFileName(path), uninstallerName, StringComparison.OrdinalIgnoreCase)));
            if (!isApp)
            {
                process.Dispose();
                continue;
            }

            try
            {
                process.Kill(entireProcessTree: true);
                killed++;
                _logger.Info("APPS | killed process | " + path);
            }
            catch (Exception exception)
            {
                _logger.Warn($"APPS | kill failed | {path} | {exception.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }

    // Остатки. Папка установки удаляется только при подтверждённом ownership:
    // деинсталлятор приложения должен лежать внутри неё (метаданные реестра
    // могут указывать на чужую папку). Папки данных в AppData/ProgramData,
    // совпадающие по имени приложения (с пробелами и без) — имя владельцем не
    // является (п. 6 аудита), они удаляются только после явного подтверждения
    // пользователем списка объектов; confirmDataCleanup=null — не удаляются.
    private int RemoveResidualFolders(
        InstalledApp app,
        string uninstallerFileName,
        Func<IReadOnlyList<string>, Task<bool>>? confirmDataCleanup,
        out List<string> failed,
        out List<string> skippedDataFolders)
    {
        failed = [];
        skippedDataFolders = [];
        var removed = 0;
        var failures = new List<string>();

        var installLocation = NormalizeDirectory(app.InstallLocation);
        if (installLocation is { Length: > 0 } installRoot
            && Directory.Exists(installRoot)
            && OwnsDirectory(uninstallerFileName, installRoot))
        {
            if (TryDeleteResidual(installRoot))
            {
                removed++;
            }
        }
        else if (installLocation is { Length: > 0 } unownedRoot && Directory.Exists(unownedRoot))
        {
            _logger.Warn("APPS | residual skipped | no ownership (uninstaller outside root) | " + unownedRoot);
        }

        var displayName = app.DisplayName.Trim();
        if (displayName.Length >= 2)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localLow = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var nameVariants = new[] { displayName, displayName.Replace(" ", string.Empty) }
                .Where(v => v.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var dataFolders = new List<string>();
            foreach (var baseDirectory in new[] { localAppData, roaming, localLow, programData })
            {
                if (string.IsNullOrEmpty(baseDirectory))
                {
                    continue;
                }

                dataFolders.AddRange(nameVariants
                    .Select(name => Path.Combine(baseDirectory, name))
                    .Where(Directory.Exists));
            }

            dataFolders = dataFolders
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // Предохранитель: папка данных обязана лежать непосредственно в
                // AppData-корне, не глубже — иначе имя могло совпасть случайно.
                .Where(directory => IsDirectChildOfKnownRoot(directory, [localAppData, roaming, localLow, programData]))
                .ToList();

            if (dataFolders.Count > 0)
            {
                var approved = confirmDataCleanup is not null && confirmDataCleanup(dataFolders).GetAwaiter().GetResult();
                if (!approved)
                {
                    skippedDataFolders.AddRange(dataFolders.Select(Path.GetFileName));
                    _logger.Info($"APPS | residual data folders skipped (not confirmed) | count={dataFolders.Count}");
                    failed = failures;
                    return removed;
                }

                foreach (var directory in dataFolders)
                {
                    if (TryDeleteResidual(directory))
                    {
                        removed++;
                    }
                }
            }
        }

        failed = failures;
        return removed;

        bool TryDeleteResidual(string directory)
        {
            try
            {
                // Предохранитель: не трогаем корни дисков и системные каталоги.
                var full = Path.GetFullPath(directory).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Length < 10
                    || string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                Directory.Delete(full, recursive: true);
                _logger.Info("APPS | residual removed | " + full);
                return true;
            }
            catch (Exception exception)
            {
                failures.Add(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)));
                _logger.Warn($"APPS | residual remove failed | {directory} | {exception.Message}");
                return false;
            }
        }
    }

    // Ownership (п. 6 аудита): папка установки подтверждается тем, что
    // деинсталлятор из uninstall-метаданных находится внутри неё. Это точный
    // признак из реестра, а не эвристика по DisplayName.
    internal static bool OwnsDirectory(string uninstallerFileName, string directory)
    {
        try
        {
            var uninstallerDirectory = NormalizeDirectory(Path.GetDirectoryName(Path.GetFullPath(uninstallerFileName)));
            return uninstallerDirectory is { Length: > 0 }
                && PathSafety.IsUnderDirectoryOrEqual(uninstallerDirectory, directory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return false;
        }
    }

    internal static bool IsDirectChildOfKnownRoot(string directory, string[] roots)
    {
        try
        {
            var full = NormalizeDirectory(directory);
            if (full is null)
            {
                return false;
            }

            foreach (var root in roots)
            {
                var normalizedRoot = NormalizeDirectory(root);
                if (normalizedRoot is null)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(normalizedRoot, full);
                if (relative != ".."
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !Path.IsPathRooted(relative)
                    && !relative.Contains(Path.DirectorySeparatorChar))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return false;
        }
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            return null;
        }
    }

    // Граница пути процесса (п. 5 аудита): StartsWith пропускает соседние
    // каталоги (C:\Apps\App2 при installRoot C:\Apps\App).
    internal static bool IsUnderDirectory(string filePath, string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var normalized = NormalizeDirectory(Path.GetDirectoryName(filePath));
        return normalized is not null && PathSafety.IsUnderDirectoryOrEqual(normalized, directory);
    }

    // Точка для регрессионных тестов границы пути (App/AppHelper/App2).
    internal static bool IsPathUnderDirectoryForTest(string? filePath, string? directory) =>
        !string.IsNullOrEmpty(filePath) && IsUnderDirectory(filePath, directory);

    // Точка для тестов ownership (деинсталлятор внутри/вне корня).
    internal static bool OwnsDirectoryForTest(string uninstallerFileName, string directory) =>
        OwnsDirectory(uninstallerFileName, directory);
}
