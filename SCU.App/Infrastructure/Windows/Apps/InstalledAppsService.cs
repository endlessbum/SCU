using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using SCU.Common;

namespace SCU.Infrastructure.Windows.Apps;

// Итог подчистки хвостов деинсталляции (п. №12 аудита): async-метод не может
// возвращать значения через out, поэтому — кортеж-запись.
internal sealed record ResidualCleanupReport(
    int Removed,
    List<string> Failed,
    List<string> SkippedDataFolders);

// Установленное приложение из ARP-раздела реестра (Add/Remove Programs).
// Показывается всё, у чего есть DisplayName, включая скрытые SystemComponent
// записи — вкладка «Приложения» задумана как полный список программ системы.
// Насколько приложение важно для системы (прочерк слева от «Удалить»).
// Правило: зелёный прочерк и активное «Удалить» — у всего, что не поставляется
// с официальной сборкой Windows (включая рекламные и навязанные корпорацией
// приложения); не-зелёный — только у компонентов самой системы.
public enum AppImportance
{
    // Приложение, не входящее в состав Windows: скачанное пользователем,
    // рекламное или предустановленное корпорацией — можно удалять.
    User,

    // Драйвер/фирменная утилита производителя железа: удалить можно, но
    // возможны проблемы в работе оборудования.
    System,

    // Компонент официальной сборки Windows: удаление может вывести систему
    // из строя, кнопка «Удалить» неактивна.
    Critical,
}

public sealed class InstalledApp : INotifyPropertyChanged
{
    public string DisplayName { get; init; } = string.Empty;
    public string? DisplayVersion { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }

    // Оценочный размер из реестра (EstimatedSize, КБ) — запасной вариант, когда
    // фактический размер папки установки вычислить нельзя (нет/битый путь).
    public long? EstimatedSizeKb { get; init; }

    // Дата установки (InstallDate из реестра, у части установщиков отсутствует
    // или нестандартна — тогда null); для сортировки по дате.
    public DateTime? InstallDate { get; init; }

    // Вычисленный размер в байтах (фактический или оценочный) — числовая
    // основа сортировки по объёму; текстовый вид живёт в SizeText.
    public long? SizeBytes { get; set; }

    // Важность для системы: считается при чтении реестра (см. Classify).
    public AppImportance Importance { get; set; } = AppImportance.User;

    // Текст размера для подписи («244 МБ»); заполняется в фоне после загрузки
    // списка — суммирование файлов сотен папок может занимать заметное время.
    // INPC: карточка в UI обновляется, как только размер готов.
    private string? _sizeText;

    public string? SizeText
    {
        get => _sizeText;
        set
        {
            if (_sizeText == value)
            {
                return;
            }

            _sizeText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString);

    // Кнопка «Удалить» доступна только для не-критичных приложений с деинсталлятором.
    public bool CanUninstall => HasUninstaller && Importance != AppImportance.Critical;

    public string DashText => "—";

    public string DashBrush => Importance switch
    {
        AppImportance.System => "WarnBrush",
        AppImportance.Critical => "TertiaryTextBrush",
        _ => "SuccessBrush",
    };

    public string DashTooltip => Importance switch
    {
        AppImportance.System => L.T(
            "Драйвер или фирменная утилита производителя железа. Удалять не рекомендуется: может нарушиться работа оборудования. При необходимости его можно установить заново."),
        AppImportance.Critical => L.T("Компонент официальной сборки Windows"),
        _ => L.T("Можно удалить"),
    };

    public string Subtitle
    {
        get
        {
            var parts = new[] { Publisher, DisplayVersion, SizeText }.Where(p => !string.IsNullOrWhiteSpace(p));
            var text = string.Join(" • ", parts);
            return text.Length > 0 ? text : "—";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
                        // EstimatedSize — REG_DWORD в килобайтах, заявленный
                        // установщиком объём; используется, если фактический
                        // размер папки вычислить нельзя.
                        EstimatedSizeKb = subKey.GetValue("EstimatedSize") is int estimatedKb && estimatedKb > 0
                            ? estimatedKb
                            : null,
                        InstallDate = ParseInstallDate((subKey.GetValue("InstallDate") as string)?.Trim()),
                        UninstallString = NormalizeUninstallString(
                            (subKey.GetValue("QuietUninstallString") as string)?.Trim()
                            ?? (subKey.GetValue("UninstallString") as string)?.Trim())
                    };
                    entry.Importance = Classify(entry,
                        IsFlagSet(subKey, "SystemComponent"),
                        IsFlagSet(subKey, "NoRemove"),
                        isPerUser: hive == RegistryHive.CurrentUser);

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

    private static bool IsFlagSet(RegistryKey key, string name) =>
        key.GetValue(name) is int value && value != 0;

    // Классификация важности для системы. Зелёный прочерк и активное «Удалить»
    // — у всех приложений, кроме компонентов официальной сборки Windows:
    // • Critical — компонент самой Windows (Edge/WebView2, VC++, .NET, DirectX,
    //   обновления, SDK и т.п.) по имени записи;
    // • System — драйвер/утилита производителя железа (жёлтый прочерк);
    // • User — всё остальное, включая рекламные и навязанные корпорацией
    //   приложения (OneDrive, Teams, Xbox и т.п.).
    // Флаги реестра на важность не влияют: SystemComponent/NoRemove у
    // сторонних и корпоративных записей — лишь политика установщика («скрыть
    // из списка», «не показывать кнопку Remove»), а не признак системности.
    internal static AppImportance Classify(
        InstalledApp entry, bool systemComponent, bool noRemove, bool isPerUser)
    {
        var name = entry.DisplayName;
        var publisher = entry.Publisher ?? string.Empty;
        var location = entry.InstallLocation ?? string.Empty;
        var haystack = $"{name} {publisher} {location} {entry.UninstallString}";

        // Игры и лаунчеры — всегда пользовательское, даже если издатель Microsoft
        // (Xbox-игры из Store) или путь в WindowsApps: проверяются первыми.
        if (IsGameOrLauncher(haystack))
        {
            return AppImportance.User;
        }

        // Скачанное пользователем: пер-установка или папка в профиле. Системные
        // компоненты в профиле пользователя не живут.
        if (isPerUser || IsUnderUserProfile(location))
        {
            return AppImportance.User;
        }

        // Компонент официальной сборки Windows — по имени записи.
        if (IsCriticalPattern(name))
        {
            return AppImportance.Critical;
        }

        // Драйвер или утилита производителя железа.
        if (IsDriverPattern(haystack))
        {
            return AppImportance.System;
        }

        // Всё прочее — пользовательское (в т.ч. навязанное корпорацией).
        return AppImportance.User;
    }

    // Папка установки в профиле пользователя (C:\Users\…, %LocalAppData%,
    // %AppData%): признак приложения, установленного самим пользователем.
    private static bool IsUnderUserProfile(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        // Неразвёрнутые переменные остаются литеральными — проверяем и их.
        return location.Contains("%LocalAppData%", StringComparison.OrdinalIgnoreCase)
            || location.Contains("%AppData%", StringComparison.OrdinalIgnoreCase)
            || Environment.ExpandEnvironmentVariables(location)
                .Contains(@"\Users\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCriticalPattern(string name) =>
        ContainsAny(name,
            [
                "Microsoft Edge", "WebView2", "Visual C++", "DirectX",
                ".NET", "DotNet", "Windows App Runtime", "Windows SDK",
                "Обновление", "Update for", "Security Update", "Накопительное",
            ],
            StringComparison.OrdinalIgnoreCase);

    // Драйвер/фирменная утилита производителя железа — единственный класс вне
    // официальной сборки Windows, которому оставлен жёлтый прочерк (не реклама
    // и не навязанное приложение, а поддержка оборудования).
    private static bool IsDriverPattern(string haystack) =>
        ContainsAny(haystack,
            [
                "NVIDIA", "AMD", "Realtek", "Intel", "Driver", "Драйвер",
            ],
            StringComparison.OrdinalIgnoreCase);

    private static bool IsGameOrLauncher(string haystack) =>
        ContainsAny(haystack,
            [
                "Steam", "Epic Games", "GOG", "Origin", "EA app", "EA Games",
                "Ubisoft", "Uplay", "Battle.net", "Blizzard", "Riot Games",
                "Rockstar", "Minecraft", "itch.io", "Epic Games Launcher",
                "лаунчер", "launcher", "игра", "game",
            ],
            StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string text, IReadOnlyList<string> patterns, StringComparison comparison) =>
        patterns.Any(pattern => text.Contains(pattern, comparison));

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

    // ===================== Размер приложений =====================

    // Фактический размер папки установки (сумма файлов рекурсивно).
    // null — путь отсутствует, не существует, это общий корень (InstallLocation
    // в реестре бывает ошибочен — суммировать весь Program Files нельзя).
    public long? GetInstallSizeBytes(InstalledApp app)
    {
        var location = NormalizeDirectory(app.InstallLocation);
        if (location is null
            || !Directory.Exists(location)
            || IsProtectedSharedDirectory(location))
        {
            return null;
        }

        try
        {
            long total = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            };
            foreach (var file in new DirectoryInfo(location).EnumerateFiles("*", options))
            {
                total += file.Length;
            }

            return total;
        }
        catch (Exception exception)
        {
            _logger.Warn($"APPS | size read failed | {location} | {exception.Message}");
            return null;
        }
    }

    // Дата установки из реестра: стандартный формат ARP — yyyyMMdd, но часть
    // установщиков пишет другие варианты; нераспознанное — null (не сортируется).
    internal static DateTime? ParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string[] formats = ["yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "dd.MM.yyyy", "M/d/yyyy"];
        return DateTime.TryParseExact(
            raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    // Размер в человекочитаемый вид («244 МБ», «1.5 ГБ») через локализацию.
    internal static string FormatSizeBytes(long bytes)
    {
        const long Kb = 1024;
        const long Mb = Kb * 1024;
        const long Gb = Mb * 1024;
        return bytes switch
        {
            >= Gb => L.T("{0:0.0} ГБ", bytes / (double)Gb),
            >= Mb => L.T("{0:0} МБ", bytes / (double)Mb),
            >= Kb => L.T("{0:0} КБ", bytes / (double)Kb),
            _ => L.T("< 1 КБ"),
        };
    }

    // ===================== Принудительное удаление с зачисткой =====================

    // Таймаут ожидания тихого деинсталлятора, после которого дерево процессов снимается.
    private const int UninstallTimeoutMinutes = 10;

    // Для интеграционных тестов graceful timeout (п. 11 аудита); прод — 10 минут.
    internal static TimeSpan UninstallTimeoutForTests { get; set; } =
        TimeSpan.FromMinutes(UninstallTimeoutMinutes);

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
        Task.Run(() => UninstallCompletelyCore(app, ct, confirmDataCleanup), CancellationToken.None);

    private async Task<Result> UninstallCompletelyCore(
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
        var uninstallExitCode = 0;
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

            // Cancellation-aware ожидание (аудит 2, п. 21): отмена пользователя
            // прерывает ожидание немедленно, а не после 10-минутного таймаута.
            // Тихие деинсталляторы завершаются сами; не уложился в таймаут —
            // снимаем процессы приложения и дерево деинсталлятора.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(UninstallTimeoutForTests);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                killed = KillAppProcesses(app);
                _logger.Warn($"APPS | uninstall cancelled | processes killed={killed} | " + app.DisplayName);
            }
            catch (OperationCanceledException)
            {
                exited = false;
                killed = KillAppProcesses(app);
                _logger.Warn($"APPS | uninstall timed out | processes killed={killed} | " + app.DisplayName);
            }

            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    _logger.Warn("APPS | uninstall tree killed | " + app.DisplayName);
                }
                catch (Exception killException)
                {
                    _logger.Warn("APPS | uninstall kill failed | " + killException.Message);
                }
            }

            if (exited)
            {
                uninstallExitCode = process.ExitCode;
            }
        }
        catch (Exception exception)
        {
            _logger.Error($"APPS | uninstall failed | {app.DisplayName} | {exception.Message}");
            return Result.Failure("Не удалось запустить удаление: " + exception.Message, 1);
        }

        // Пользовательская отмена: ничего не удаляем после снятия деинсталлятора.
        ct.ThrowIfCancellationRequested();

        // Аудит 3, п. 9: нормальное завершение процесса ≠ успешное удаление.
        // Код возврата деинсталлятора (например 1603 у MSI) анализируется:
        // при сбое хвосты НЕ зачищаются — приложение могло остаться установленным,
        // и удаление папки установки вслепую опасно.
        if (exited && uninstallExitCode != 0)
        {
            _logger.Warn($"APPS | uninstall exit code | {app.DisplayName} | rc={uninstallExitCode}");
            return Result.Failure(
                $"Деинсталлятор «{app.DisplayName}» завершился с ошибкой (код {uninstallExitCode}). "
                + "Приложение могло остаться установленным — повторите удаление или удалите вручную.",
                uninstallExitCode);
        }

        // Хвосты: папка установки (только при подтверждённом ownership) и папки
        // данных, совпадающие по имени приложения — после явного подтверждения.
        var residual = await RemoveResidualFolders(app, fileName, confirmDataCleanup).ConfigureAwait(false);
        var removed = residual.Removed;
        var failedFolders = residual.Failed;
        var skippedDataFolders = residual.SkippedDataFolders;

        // Аудит 3, п. 10: завершённый деинсталлятор ≠ полностью удалённое
        // приложение — неочищенные хвосты явно попадают в итоговый текст.
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
        if (skippedDataFolders.Count > 0 || failedFolders.Count > 0)
        {
            summary += " Удаление выполнено частично.";
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

    // Процессы приложения в текущей сессии: только исполняемые файлы из
    // папки установки. Совпадение имени деинсталлятора по всей сессии
    // (unins000.exe / msiexec) убивало бы чужие деревья процессов.
    private int KillAppProcesses(InstalledApp app)
    {
        var installLocation = NormalizeDirectory(app.InstallLocation);
        if (string.IsNullOrEmpty(installLocation) || IsProtectedSharedDirectory(installLocation))
        {
            return 0;
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

            if (!IsAppProcessPath(path, installLocation))
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
    // П. №12 аудита: подтверждение очистки теперь await-ится по-настоящему
    // (sync-over-async с GetResult() создавал риск дедлока). out-параметры
    // заменены кортежем — async-методы не могут иметь out/ref.
    private async Task<ResidualCleanupReport> RemoveResidualFolders(
        InstalledApp app,
        string uninstallerFileName,
        Func<IReadOnlyList<string>, Task<bool>>? confirmDataCleanup)
    {
        var failed = new List<string>();
        var skippedDataFolders = new List<string>();
        var removed = 0;
        var failures = new List<string>();

        var installLocation = NormalizeDirectory(app.InstallLocation);
        if (installLocation is { Length: > 0 } installRoot
            && Directory.Exists(installRoot)
            && !IsProtectedSharedDirectory(installRoot)
            && OwnsDirectory(uninstallerFileName, installRoot))
        {
            if (TryDeleteResidual(installRoot))
            {
                removed++;
            }
        }
        else if (installLocation is { Length: > 0 } unownedRoot && Directory.Exists(unownedRoot))
        {
            _logger.Warn("APPS | residual skipped | no ownership or shared root | " + unownedRoot);
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
                // Общие каталоги (Microsoft, Windows, Temp) не удаляются даже по имени.
                .Where(directory => IsDirectChildOfKnownRoot(directory, [localAppData, roaming, localLow, programData])
                    && !IsProtectedDataFolderName(directory)
                    && !IsProtectedSharedDirectory(directory))
                .ToList();

            if (dataFolders.Count > 0)
            {
                var approved = confirmDataCleanup is not null && await confirmDataCleanup(dataFolders).ConfigureAwait(false);
                if (!approved)
                {
                    skippedDataFolders.AddRange(dataFolders.Select(folder => Path.GetFileName(folder) ?? folder));
                    _logger.Info($"APPS | residual data folders skipped (not confirmed) | count={dataFolders.Count}");
                    return new ResidualCleanupReport(removed, failures, skippedDataFolders);
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

        return new ResidualCleanupReport(removed, failures, skippedDataFolders);

        bool TryDeleteResidual(string directory)
        {
            try
            {
                // Предохранитель: не трогаем корни дисков и системные каталоги.
                var full = Path.GetFullPath(directory).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Length < 10
                    || string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase)
                    || IsProtectedSharedDirectory(full))
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

    // Широкие/общие корни (п. 6 аудита): их нельзя удалять как «папку установки»,
    // даже если ownership формально подтверждён — InstallLocation в реестре бывает
    // ошибочен, а деинсталлятор лежит прямо в C:\Program Files. Кандидат защищён,
    // если он сам общий корень или его предок (вплоть до корня диска); обычная
    // папка приложения-потомка (C:\Program Files\MyApp) защитой не считается.
    private static readonly string[] SharedRoots =
    [
        "Windows",
        @"Windows\System32",
        @"Windows\SysWOW64",
        "Program Files",
        @"Program Files\Common Files",
        "Program Files (x86)",
        @"Program Files (x86)\Common Files",
        "ProgramData",
        "Users",
    ];

    internal static bool IsProtectedSharedDirectory(string? directory)
    {
        var full = NormalizeDirectory(directory);
        if (full is null)
        {
            return false;
        }

        var anchor = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(anchor))
        {
            return false;
        }

        if (string.Equals(
                anchor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                full,
                StringComparison.OrdinalIgnoreCase))
        {
            return true; // корень диска
        }

        foreach (var shared in SharedRoots)
        {
            var sharedRoot = NormalizeDirectory(Path.Combine(anchor, shared));
            if (sharedRoot is not null && PathSafety.IsUnderDirectoryOrEqual(sharedRoot, full))
            {
                return true; // candidate — сам общий корень или его предок
            }
        }

        return false;
    }

    // Имена, которые папкой данных приложения не бывают (п. 6 аудита): совпадение
    // DisplayName с ними случайно, удаление по имени недопустимо в принципе.
    private static readonly IReadOnlyList<string> ProtectedDataFolderNames =
    [
        "Microsoft", "Windows", "Common Files", "ProgramData", "Program Files",
        "Program Files (x86)", "Users", "Temp", "Packages", "Desktop", "Documents",
    ];

    internal static bool IsProtectedDataFolderName(string directory)
    {
        var name = Path.GetFileName(
            directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return ProtectedDataFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase);
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

    // Процесс приложения: путь его исполняемого файла лежит в папке установки
    // (граница пути через PathSafety — соседние каталоги и reparse-переходы
    // наружу отсекаются).
    internal static bool IsAppProcessPath(string? filePath, string? directory) =>
        !string.IsNullOrEmpty(filePath) && IsUnderDirectory(filePath, directory);

    // Точка для регрессионных тестов границы пути (App/AppHelper/App2).
    internal static bool IsPathUnderDirectoryForTest(string? filePath, string? directory) =>
        !string.IsNullOrEmpty(filePath) && IsUnderDirectory(filePath, directory);

    // Точка для тестов ownership (деинсталлятор внутри/вне корня).
    internal static bool OwnsDirectoryForTest(string uninstallerFileName, string directory) =>
        OwnsDirectory(uninstallerFileName, directory);

    // Точка для тестов защиты общих каталогов (п. 6 аудита).
    internal static bool IsProtectedSharedDirectoryForTest(string? directory) =>
        IsProtectedSharedDirectory(directory);

    internal static bool IsProtectedDataFolderNameForTest(string directory) =>
        IsProtectedDataFolderName(directory);

    // Точка для тестов безопасности снятия процессов (п. 11 аудита).
    internal int KillAppProcessesForTest(InstalledApp app) => KillAppProcesses(app);
}
