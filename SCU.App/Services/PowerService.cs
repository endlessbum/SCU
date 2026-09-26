using System.Globalization;
using System.Management;
using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.Services;

// Раздел 8 «Utilities.bat» — питание, память, CPU.
// powercfg/bcdedit — через LongProcessRunner, реестр — через RegistryHelper, WMI — напрямую.
public sealed class PowerService
{
    public static readonly string HighPerformancePlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public static readonly string UltimatePerformancePlanGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public static readonly string BalancedPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    // Штатная «Экономия энергии» и план Bitsum Highest Performance (создаётся Process Lasso /
    // ParkControl; если схемы нет — powercfg -duplicatescheme по шаблону, как для остальных).
    public static readonly string PowerSaverPlanGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public static readonly string BitsumHighestPerformancePlanGuid = "19cfaaf6-8487-4fd0-809e-3b5b1ec82302";

    // Стабильные GUID для копий скрытых/сторонних шаблонов. powercfg позволяет
    // передать destination GUID в /duplicatescheme, поэтому повторные запуски
    // не порождают новые схемы даже после удаления/повреждения plans.json.
    // Публичные: VM подсвечивает чипы схем по GUID фактически активной копии.
    public const string UltimatePerformanceCopyGuid = "f16f20f1-4372-42ce-8601-8a3500053cb6";
    public const string BitsumHighestPerformanceCopyGuid = "bea1bc1e-5df4-4697-8ddb-44826e5b3fdc";

    private readonly Logger _logger;
    private readonly LongProcessRunner _runner;
    private readonly RegistryHelper _registry;

    public PowerService(Logger logger, LongProcessRunner runner, RegistryHelper registry)
    {
        _logger = logger;
        _runner = runner;
        _registry = registry;
    }

    private static IReadOnlyList<RegistryTweak> FastBootTweak =>
    [
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Power",
            "HiberbootEnabled",
            RegistryValueKind.DWord,
            0, 1)
    ];

    // Активация плана: канонические схемы (Balanced и т.п.) матчатся по GUID напрямую.
    // Для шаблонов Ultimate/Bitsum, которых нет в /list, используется стабильный
    // destination GUID. Это убирает зависимость от случайного GUID и от plans.json.
    public async Task<Result> SetPowerPlanAsync(string templateGuid, CancellationToken ct = default)
    {
        var list = await _runner.RunAsync("powercfg", ["/list"], null, ct).ConfigureAwait(false);
        if (!list.IsSuccess)
        {
            return Result.Failure("Не удалось прочитать список планов: " + list.Message, list.Code);
        }

        var guids = LongProcessRunner.ExtractGuids(list.Value ?? string.Empty);
        var activeGuid = guids.FirstOrDefault(g =>
            string.Equals(g, templateGuid, StringComparison.OrdinalIgnoreCase));

        if (activeGuid is null)
        {
            var stableDestination = GetStableDuplicateGuid(templateGuid);

            // Поддержка схем, созданных старой версией приложения: если старый
            // случайный GUID ещё жив, используем его вместо дополнительного клона.
            var plans = LoadDuplicatePlans();
            if (plans.TryGetValue(templateGuid, out var remembered)
                && guids.Any(g => string.Equals(g, remembered, StringComparison.OrdinalIgnoreCase)))
            {
                activeGuid = remembered;
            }
            else if (stableDestination is not null
                && guids.Any(g => string.Equals(g, stableDestination, StringComparison.OrdinalIgnoreCase)))
            {
                activeGuid = stableDestination;
            }
            else
            {
                var duplicateArgs = stableDestination is null
                    ? new[] { "-duplicatescheme", templateGuid }
                    : new[] { "-duplicatescheme", templateGuid, stableDestination };

                var duplicate = await _runner
                    .RunAsync("powercfg", duplicateArgs, null, ct)
                    .ConfigureAwait(false);

                if (!duplicate.IsSuccess)
                {
                    // Безопасно переживаем гонку с другим экземпляром приложения:
                    // если схема успела появиться, переиспользуем её, а не клонируем ещё раз.
                    var retryList = await _runner.RunAsync("powercfg", ["/list"], null, ct)
                        .ConfigureAwait(false);
                    var retryGuids = retryList.IsSuccess
                        ? LongProcessRunner.ExtractGuids(retryList.Value ?? string.Empty)
                        : [];
                    var duplicateGuid = stableDestination ?? LongProcessRunner.ExtractGuids(duplicate.Value ?? string.Empty).LastOrDefault();
                    activeGuid = duplicateGuid is not null && retryGuids.Any(g =>
                        string.Equals(g, duplicateGuid, StringComparison.OrdinalIgnoreCase))
                        ? duplicateGuid
                        : null;

                    if (activeGuid is null)
                    {
                        return Result.Failure("Не удалось создать копию плана: " + duplicate.Message, duplicate.Code);
                    }
                }
                else
                {
                    activeGuid = stableDestination
                        ?? LongProcessRunner.ExtractGuids(duplicate.Value ?? string.Empty).LastOrDefault();
                    if (activeGuid is null)
                    {
                        return Result.Failure("powercfg не вернул GUID нового плана.");
                    }

                    if (stableDestination is not null)
                    {
                        plans[templateGuid] = stableDestination;
                        SaveDuplicatePlans(plans);
                    }
                }
            }
        }

        var activate = await _runner
            .RunAsync("powercfg", ["/setactive", activeGuid], null, ct)
            .ConfigureAwait(false);
        if (!activate.IsSuccess)
        {
            return Result.Failure(activate.Message, activate.Code);
        }

        var verify = await _runner
            .RunAsync("powercfg", ["/getactivescheme"], null, ct)
            .ConfigureAwait(false);
        if (!verify.IsSuccess)
        {
            return Result.Failure("План активирован, но не удалось подтвердить его чтением: " + verify.Message, verify.Code);
        }

        var verifiedGuid = LongProcessRunner.ExtractGuids(verify.Value ?? string.Empty).FirstOrDefault();
        if (!string.Equals(verifiedGuid, activeGuid, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure($"Активная схема не совпала: фактически {verifiedGuid ?? "не определена"}, ожидалось {activeGuid}.");
        }

        _logger.Info($"POWER | plan {activeGuid} activated and verified");
        return Result.Success("План электропитания установлен и подтверждён.");
    }

    public async Task<Result> SetHibernationAsync(bool enable, CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync("powercfg", ["/h", enable ? "on" : "off"], null, ct)
            .ConfigureAwait(false);
        _logger.Info($"POWER | hibernation={enable} | rc={result.Code}");
        return result.IsSuccess
            ? Result.Success(enable ? "Гибернация включена." : "Гибернация отключена.")
            : Result.Failure("powercfg завершился с кодом " + result.Code, result.Code);
    }

    public Result SetFastBoot(bool disable)
    {
        var tweak = FastBootTweak[0] with { OffValue = disable ? 0 : 1 };
        var result = _registry.Apply([tweak], BackupPath("fastboot.json"));
        if (!result.IsSuccess)
            return result;

        // Подтверждение записью: HiberbootEnabled должен совпасть с ожидаемым.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            var actual = key?.GetValue("HiberbootEnabled") as int?;
            var expected = disable ? 0 : 1;
            if (actual != expected)
            {
                return Result.Failure(
                    "HiberbootEnabled записан, но повторное чтение не подтвердило значение. " +
                    "Проверьте права администратора.");
            }
        }
        catch (Exception ex)
        {
            return Result.Failure("Не удалось подтвердить HiberbootEnabled после записи: " + ex.Message);
        }

        // Полное применение hybrid shutdown — после перезагрузки; вызывающий UI ставит PendingReboot.
        return Result.Success(disable
            ? "Быстрый запуск отключён (требуется перезагрузка)."
            : "Быстрый запуск включён (требуется перезагрузка).");
    }

    /// <summary>
    /// Совместимость: true, если HiberbootEnabled == 0.
    /// Предпочтительно использовать <see cref="GetFastStartupStateAsync"/>.
    /// </summary>
    public static bool IsFastBootDisabled(RegistryHelper registry) => registry.IsApplied(FastBootTweak);

    /// <summary>
    /// Фактическое состояние Fast Startup.
    /// Учитывает HiberbootEnabled, наличие/тип hiberfile и доступность гибридного сна (powercfg /a).
    /// Не сводит ошибку чтения к false.
    /// </summary>
    public async Task<Result<FastStartupInfo>> GetFastStartupStateAsync(CancellationToken ct = default)
    {
        try
        {
            int? hiberboot = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
                hiberboot = key?.GetValue("HiberbootEnabled") as int?;
            }
            catch (UnauthorizedAccessException)
            {
                return Result<FastStartupInfo>.Failure("Нет доступа к реестру HiberbootEnabled.", 5);
            }
            catch (Exception ex)
            {
                return Result<FastStartupInfo>.Failure("Не удалось прочитать HiberbootEnabled: " + ex.Message);
            }

            // powercfg /a — какие состояния сна реально доступны.
            var available = await _runner
                .RunAsync("powercfg", ["/a"], null, ct)
                .ConfigureAwait(false);

            var hybridAvailable = false;
            var hibernateAvailable = false;
            if (available.IsSuccess && available.Value is not null)
            {
                var text = available.Value;
                // Англ. и локализованные формулировки.
                hybridAvailable = text.Contains("Hybrid Sleep", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Гибридный спящий", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Fast Startup", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Быстрый запуск", StringComparison.OrdinalIgnoreCase);
                hibernateAvailable = text.Contains("Hibernate", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Гибернац", StringComparison.OrdinalIgnoreCase);
            }

            // Тип hiberfile: full / reduced / none (через размер файла и HibernateEnabledDefault / HiberFileSizePercent).
            var hiberFileType = DetectHiberfileType();

            // Pending reboot: реестр уже другой, а runtime (hiberfile / powercfg) ещё старый.
            var requiresReboot = false;
            SystemSettingState state;
            if (hiberboot is null && !available.IsSuccess)
            {
                state = SystemSettingState.Unknown;
            }
            else if (hiberboot is 1 && hiberFileType == HiberfileType.None)
            {
                // Записано «включено», файла нет — либо недоступно, либо ждёт применения после reboot.
                // Если hibernate в powercfg тоже недоступна, скорее Unavailable; иначе PendingReboot.
                if (!hibernateAvailable && available.IsSuccess)
                {
                    state = SystemSettingState.Unavailable;
                }
                else
                {
                    state = SystemSettingState.PendingReboot;
                    requiresReboot = true;
                }
            }
            else if (hiberboot is 0 && hiberFileType is HiberfileType.Reduced or HiberfileType.Full)
            {
                // Реестр «выкл», но hiberfile ещё на месте — полное применение после reboot.
                state = SystemSettingState.PendingReboot;
                requiresReboot = true;
            }
            else if (hiberboot is 0)
            {
                state = SystemSettingState.Disabled;
            }
            else if (hiberboot is 1 || hiberboot is null)
            {
                // null = значение по умолчанию Windows (обычно включено на клиентских SKU).
                state = SystemSettingState.Enabled;
            }
            else
            {
                state = SystemSettingState.Unknown;
            }

            return Result<FastStartupInfo>.Success(new FastStartupInfo(
                state,
                hiberboot,
                hiberFileType,
                hybridAvailable,
                hibernateAvailable,
                RequiresReboot: requiresReboot));
        }
        catch (OperationCanceledException)
        {
            return Result<FastStartupInfo>.Failure("Отменено", -1);
        }
        catch (Exception ex)
        {
            return Result<FastStartupInfo>.Failure(ex.Message);
        }
    }

    private static HiberfileType DetectHiberfileType()
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var hiberPath = Path.Combine(systemDrive, "hiberfil.sys");
            if (!File.Exists(hiberPath))
            {
                // Файл может быть скрыт/system — пробуем атрибуты через Directory.
                try
                {
                    var info = new FileInfo(hiberPath);
                    if (!info.Exists)
                        return HiberfileType.None;
                }
                catch
                {
                    return HiberfileType.None;
                }
            }

            // HiberFileType в реестре (Windows 10+): 0 = full, 1 = reduced (Fast Startup only).
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            var type = key?.GetValue("HiberFileType") as int?;
            return type switch
            {
                0 => HiberfileType.Full,
                1 => HiberfileType.Reduced,
                _ => HiberfileType.Unknown
            };
        }
        catch
        {
            return HiberfileType.Unknown;
        }
    }

    /// <summary>
    /// Только чтение: включена ли гибернация.
    /// Ошибка доступа → не «включено по умолчанию», а отдельный сбой (вызывающий код обрабатывает).
    /// </summary>
    public static bool? TryIsHibernationEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            return key?.GetValue("HibernateEnabled") switch
            {
                int intValue => intValue != 0,
                null => true, // отсутствие ключа = поведение по умолчанию Windows
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Совместимость: при ошибке чтения возвращает true (старое поведение). Предпочтительно TryIsHibernationEnabled.</summary>
    public static bool IsHibernationEnabled() => TryIsHibernationEnabled() ?? true;

    // Файл подкачки: отключение автозатем управления и фиксация размера (аналог :PFApply).
    // Изменения применяются после перезагрузки. Прежние настройки сохраняются в резерв,
    // путь берётся существующий (или системный диск) — не жёстко C:\.
    public async Task<Result> SetPageFileAsync(int sizeMb, CancellationToken ct = default)
    {
        if (sizeMb is < 16 or > 262144)
        {
            return Result.Failure("Размер файла подкачки должен быть в диапазоне 16..262144 МБ.");
        }

        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                using var computerSystemClass = new ManagementClass("Win32_ComputerSystem");
                using var systems = computerSystemClass.GetInstances();
                using var system = systems.OfType<ManagementObject>().First();
                var automatic = (bool)system["AutomaticManagedPagefile"];

                // Backup должен существовать ДО любого изменения системы.
                var backup = BackupPageFileSettings(automatic);
                if (!backup.IsSuccess)
                {
                    return backup;
                }

                if (automatic)
                {
                    system["AutomaticManagedPagefile"] = false;
                    system.Put();
                    _logger.Info("PAGEFILE | automatic management disabled");
                }
                else
                {
                    _logger.Info("PAGEFILE | automatic management already off");
                }

                // Системный диск вместо жёстко вшитого C: — на системах с Windows на другом томе
                // старый вариант ломал файл подкачки после перезагрузки.
                var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                var defaultPageFile = Path.Combine(systemDrive, "pagefile.sys");

                using var pageFileClass = new ManagementClass("Win32_PageFileSetting");
                using var pageFileSettings = pageFileClass.GetInstances();
                var memories = pageFileSettings.OfType<ManagementObject>().ToList();
                if (memories.Count == 0)
                {
                    // Ключ PagingFiles пишем напрямую — Win32_PageFileSetting может не существовать.
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", writable: true);
                    if (key is null)
                    {
                        return Result.Failure("Не удалось открыть раздел реестра для настройки файла подкачки.");
                    }

                    var expected = $"{defaultPageFile} {sizeMb} {sizeMb}";
                    key.SetValue("PagingFiles", new[] { expected }, RegistryValueKind.MultiString);
                    var actual = key.GetValue("PagingFiles") as string[] ?? [];
                    if (!actual.Contains(expected, StringComparer.OrdinalIgnoreCase))
                    {
                        return Result.Failure("Настройка файла подкачки не подтвердилась чтением реестра.");
                    }
                }
                else
                {
                    // При нескольких pagefile изменяем ВСЕ существующие файлы, сохраняя их расположение.
                    // Список затрагиваемых путей собираем до dispose объектов WMI.
                    var affectedPaths = new List<string>();
                    foreach (var memory in memories)
                    {
                        using (memory)
                        {
                            var existingName = memory["Name"] as string;
                            if (string.IsNullOrWhiteSpace(existingName))
                            {
                                existingName = defaultPageFile;
                            }

                            affectedPaths.Add(existingName);
                            memory["Name"] = existingName;
                            memory["InitialSize"] = sizeMb;
                            memory["MaximumSize"] = sizeMb;
                            memory.Put();
                            memory.Get();

                            var actualInitial = Convert.ToInt32(memory["InitialSize"] ?? 0, CultureInfo.InvariantCulture);
                            var actualMaximum = Convert.ToInt32(memory["MaximumSize"] ?? 0, CultureInfo.InvariantCulture);
                            if (actualInitial != sizeMb || actualMaximum != sizeMb)
                            {
                                return Result.Failure($"Размер pagefile {existingName} не подтвердился после записи.");
                            }
                        }
                    }

                    var affected = string.Join(", ", affectedPaths);
                    var crash = ReadCrashDumpInfo();
                    var crashNote = crash.MayRequirePagefile
                        ? " Внимание: текущая конфигурация дампа памяти может требовать достаточный pagefile."
                        : "";
                    _logger.Info($"PAGEFILE | set {sizeMb} MB | files={affectedPaths.Count} | affected={affected}");
                    return Result.Success(
                        $"Файл подкачки: {sizeMb} МБ на томах: {affected}. Вступит в силу после перезагрузки.{crashNote}");
                }

                {
                    var crash = ReadCrashDumpInfo();
                    var crashNote = crash.MayRequirePagefile
                        ? " Внимание: текущая конфигурация дампа памяти может требовать достаточный pagefile."
                        : "";
                    _logger.Info($"PAGEFILE | set {sizeMb} MB | registry path={defaultPageFile}");
                    return Result.Success(
                        $"Файл подкачки: {sizeMb} МБ ({defaultPageFile}). Вступит в силу после перезагрузки.{crashNote}");
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось настроить файл подкачки: " + exception.Message);
        }
    }

    // Резерв перед КАЖДЫМ деструктивным изменением pagefile (не «один раз навсегда»).
    // JSON: original + timestamp; старый .txt не перезаписываем поверх без версии.
    private Result BackupPageFileSettings(bool automaticManaged)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            if (key is null)
            {
                return Result.Failure("Не удалось открыть раздел реестра для резервирования файла подкачки.");
            }

            var pagingFiles = key.GetValue("PagingFiles") as string[] ?? [];
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCU", "backup", "power");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var backupFile = Path.Combine(dir, $"pagefile_backup_{stamp}.json");

            var pagefileEntries = new List<object>();
            foreach (var entry in pagingFiles)
            {
                // Формат: "C:\pagefile.sys 2048 4096" или "C:\pagefile.sys"
                var parts = entry.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                var path = parts.Length > 0 ? parts[0] : entry;
                int? initial = parts.Length > 1 && int.TryParse(parts[1], out var i) ? i : null;
                int? maximum = parts.Length > 2 && int.TryParse(parts[2], out var m) ? m : null;
                pagefileEntries.Add(new
                {
                    path,
                    initialSize = initial,
                    maximumSize = maximum,
                    systemManaged = automaticManaged
                });
            }

            var dto = new
            {
                version = 1,
                createdAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                automaticManagedPagefile = automaticManaged,
                pagefiles = pagefileEntries
            };

            var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(backupFile, json);

            // Актуальная «текущая» копия для быстрого отката.
            var latest = Path.Combine(dir, "pagefile_backup_latest.json");
            File.WriteAllText(latest, json);

            if (!File.Exists(backupFile))
            {
                return Result.Failure("Резерв файла подкачки не был создан.");
            }

            _logger.Info("PAGEFILE | backup -> " + backupFile);
            return Result.Success("Резерв сохранён: " + Path.GetFileName(backupFile));
        }
        catch (Exception exception)
        {
            _logger.Error("PAGEFILE | backup failed | " + exception);
            return Result.Failure("Не удалось сохранить резерв файла подкачки: " + exception.Message);
        }
    }

    /// <summary>
    /// Структурированное состояние pagefile + crash dump.
    /// Не подставляет «рекомендуемый» размер по объёму RAM.
    /// </summary>
    public async Task<Result<PageFileInfo>> GetPageFileStateAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
                var pagingFiles = key?.GetValue("PagingFiles") as string[] ?? [];
                var automatic = false;
                using (var computerSystemClass = new ManagementClass("Win32_ComputerSystem"))
                using (var systems = computerSystemClass.GetInstances())
                using (var system = systems.OfType<ManagementObject>().FirstOrDefault())
                {
                    automatic = system is not null && (bool)system["AutomaticManagedPagefile"];
                }

                var entries = new List<PageFileEntry>();
                foreach (var entry in pagingFiles)
                {
                    var parts = entry.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    var path = parts.Length > 0 ? parts[0] : entry;
                    int? initial = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
                    int? maximum = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : null;
                    entries.Add(new PageFileEntry(path, initial, maximum));
                }

                var crash = ReadCrashDumpInfo();
                return Result<PageFileInfo>.Success(new PageFileInfo(
                    automatic,
                    entries,
                    crash.MayRequirePagefile,
                    crash.Summary));
            }, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Result<PageFileInfo>.Failure(exception.Message);
        }
    }

    /// <summary>Совместимость: строка для старого UI/парсера.</summary>
    public async Task<Result<string>> GetPageFileInfoAsync(CancellationToken ct = default)
    {
        var state = await GetPageFileStateAsync(ct).ConfigureAwait(false);
        if (!state.IsSuccess || state.Value is null)
            return Result<string>.Failure(state.Message, state.Code);

        var pf = state.Value;
        var list = pf.Entries.Count > 0
            ? string.Join("; ", pf.Entries.Select(e =>
                e.InitialSizeMb is null
                    ? e.Path
                    : $"{e.Path} {e.InitialSizeMb} {e.MaximumSizeMb}"))
            : "(не задано)";
        return Result<string>.Success($"Automatic={pf.SystemManaged} | PagingFiles={list}");
    }

    /// <summary>
    /// CrashControl: 0=None, 1=Complete, 2=Kernel, 3=Small, 7=Automatic.
    /// Complete (1) и Kernel (2) обычно требуют достаточного pagefile.
    /// </summary>
    public static CrashDumpInfo ReadCrashDumpInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\CrashControl");
            var enabled = key?.GetValue("CrashDumpEnabled") as int? ?? 0;
            var dumpFile = key?.GetValue("DumpFile") as string;
            // 1 = Complete memory dump, 2 = Kernel memory dump — зависят от pagefile.
            var mayRequire = enabled is 1 or 2;
            var kind = enabled switch
            {
                0 => "нет",
                1 => "полный (Complete)",
                2 => "ядра (Kernel)",
                3 => "малый (Small)",
                7 => "автоматический (Automatic)",
                _ => $"код {enabled}"
            };
            return new CrashDumpInfo(
                enabled,
                dumpFile,
                mayRequire,
                $"Дамп памяти: {kind}" + (string.IsNullOrEmpty(dumpFile) ? "" : $", файл: {dumpFile}"));
        }
        catch
        {
            return new CrashDumpInfo(0, null, false, "Дамп памяти: не удалось прочитать");
        }
    }

    /// <summary>
    /// Вернуть управление размером pagefile Windows (System Managed).
    /// Backup перед изменением; проверка AutomaticManagedPagefile после записи.
    /// </summary>
    public async Task<Result> SetSystemManagedPageFileAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var computerSystemClass = new ManagementClass("Win32_ComputerSystem");
                using var systems = computerSystemClass.GetInstances();
                using var system = systems.OfType<ManagementObject>().First();
                var automatic = (bool)system["AutomaticManagedPagefile"];

                var backup = BackupPageFileSettings(automatic);
                if (!backup.IsSuccess)
                    return backup;

                if (automatic)
                    return Result.Success("Файл подкачки уже управляется Windows.");

                system["AutomaticManagedPagefile"] = true;
                system.Put();

                // Перечитать
                system.Get();
                var now = (bool)system["AutomaticManagedPagefile"];
                if (!now)
                    return Result.Failure("Не удалось включить System Managed pagefile (проверка после записи).");

                _logger.Info("PAGEFILE | system managed enabled");
                return Result.Success("Управление файлом подкачки передано Windows. Вступит в силу после перезагрузки.");
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось включить System Managed pagefile: " + exception.Message);
        }
    }

    // Снятие ограничений numproc/truncatememory через bcdedit.
    // Обязательно: backup (export) → изменение → повторное чтение → verify.
    public async Task<Result> ClearCpuMemoryLimitsAsync(CancellationToken ct = default)
    {
        var status = await _runner
            .RunAsync("bcdedit", ["/enum", "{current}"], null, ct)
            .ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            return Result.Failure("Не удалось прочитать BCD: " + status.Message, status.Code);
        }

        var output = status.Value ?? string.Empty;
        var hasNumproc = ContainsBcdValue(output, "numproc");
        var hasTruncate = ContainsBcdValue(output, "truncatememory");

        if (!hasNumproc && !hasTruncate)
        {
            return Result.Success("Ограничения CPU/ОЗУ в BCD уже отсутствуют.");
        }

        // Backup ДО изменения. Без успешного export изменение отменяем.
        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU", "backup", "bcd");
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var exportPath = Path.Combine(backupDir, $"bcd_export_{stamp}");
        var export = await _runner
            .RunAsync("bcdedit", ["/export", exportPath], null, ct)
            .ConfigureAwait(false);
        if (!export.IsSuccess)
        {
            _logger.Warn("BCD | export failed | " + export.Message);
            return Result.Failure(
                "Не удалось создать резервную копию BCD — изменение отменено: " + export.Message,
                export.Code);
        }

        _logger.Info("BCD | export -> " + exportPath);

        // Сохраняем исходные значения параметров для аудита.
        try
        {
            var metaPath = exportPath + ".meta.json";
            var meta = System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 1,
                createdAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                bootIdentifier = "{current}",
                parameters = new
                {
                    numproc = hasNumproc ? ExtractBcdValue(output, "numproc") : null,
                    truncatememory = hasTruncate ? ExtractBcdValue(output, "truncatememory") : null
                },
                exportPath
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, meta);
        }
        catch (Exception ex)
        {
            _logger.Warn("BCD | meta save failed | " + ex.Message);
        }

        if (hasNumproc)
        {
            var delete = await _runner
                .RunAsync("bcdedit", ["/deletevalue", "{current}", "numproc"], null, ct)
                .ConfigureAwait(false);
            _logger.Info("BCD | delete numproc | rc=" + delete.Code);
            if (!delete.IsSuccess)
            {
                return Result.Failure(delete.Message, delete.Code);
            }
        }

        if (hasTruncate)
        {
            var delete = await _runner
                .RunAsync("bcdedit", ["/deletevalue", "{current}", "truncatememory"], null, ct)
                .ConfigureAwait(false);
            _logger.Info("BCD | delete truncatememory | rc=" + delete.Code);
            if (!delete.IsSuccess)
            {
                return Result.Failure(delete.Message, delete.Code);
            }
        }

        var verify = await _runner
            .RunAsync("bcdedit", ["/enum", "{current}"], null, ct)
            .ConfigureAwait(false);
        if (!verify.IsSuccess)
        {
            return Result.Failure("Не удалось повторно проверить BCD: " + verify.Message, verify.Code);
        }

        var verifyOutput = verify.Value ?? string.Empty;
        if (ContainsBcdValue(verifyOutput, "numproc") || ContainsBcdValue(verifyOutput, "truncatememory"))
        {
            return Result.Failure("BCD: ограничения не сняты после удаления (проверка чтением не прошла).");
        }

        _logger.Info("BCD | numproc/truncatememory absent | verified");
        return Result.Success("Ограничения CPU/ОЗУ в BCD сняты. Резервная копия создана. Требуется перезагрузка Windows.");
    }

    // Только чтение: GUID активной схемы (powercfg /getactivescheme).
    public async Task<Result<string>> GetActivePlanGuidAsync(CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync("powercfg", ["/getactivescheme"], null, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result<string>.Failure(result.Message, result.Code);
        }

        var guid = LongProcessRunner.ExtractGuids(result.Value ?? string.Empty).FirstOrDefault();
        return guid is null
            ? Result<string>.Failure("powercfg не вернул GUID активной схемы.")
            : Result<string>.Success(guid);
    }

    // Только чтение: компактная сводка ограничений BCD (numproc/truncatememory)
    // и температуры CPU — вместо полного дампа powercfg /query.
    public async Task<Result<string>> GetCpuLimitsSummaryAsync(CancellationToken ct = default)
    {
        var status = await _runner
            .RunAsync("bcdedit", ["/enum", "{current}"], null, ct)
            .ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            return Result<string>.Failure("Не удалось прочитать BCD: " + status.Message, status.Code);
        }

        var output = status.Value ?? string.Empty;
        var numproc = ExtractBcdValue(output, "numproc");
        var truncate = ExtractBcdValue(output, "truncatememory");

        var lines = new List<string>
        {
            numproc is null ? "numproc — не задано" : $"numproc — {numproc}",
            truncate is null ? "truncatememory — не задано" : $"truncatememory — {truncate}"
        };

        // Источник — MSAcpi_ThermalZoneTemperature, не датчик CPU Package.
        // Название в UI должно отражать реальный источник.
        var temperature = await GetThermalZoneTemperatureAsync(ct).ConfigureAwait(false);
        var formatted = FormatThermalZoneTemperature(temperature);
        if (formatted is not null)
        {
            lines.Add("Температура по ACPI Thermal Zone: " + formatted);
        }
        else
        {
            lines.Add("Температура по ACPI Thermal Zone: нет данных");
        }

        return Result<string>.Success(string.Join(Environment.NewLine, lines));
    }

    // Сырой вывод ("ACPI\ThermalZone\TZ00_0: 27,9 C") → «27,9°» первой зоны.
    // null — датчики не отвечают (не подставляем 0 °C).
    private static string? FormatThermalZoneTemperature(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('('))
        {
            return null;
        }

        var first = raw.Split('\n')[0].Trim();
        var colon = first.LastIndexOf(':');
        var value = (colon >= 0 ? first[(colon + 1)..] : first).Trim();
        if (value.EndsWith(" C", StringComparison.Ordinal))
        {
            value = value[..^2].TrimEnd();
        }

        return value + "°";
    }

    private static string? ExtractBcdValue(string output, string valueName)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(valueName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[^1] : null;
        }

        return null;
    }

    /// <summary>
    /// Температура ACPI Thermal Zone (не CPU Package).
    /// При отсутствии датчика — «нет данных», не 0 °C.
    /// </summary>
    public async Task<string> GetThermalZoneTemperatureAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var searcher = new ManagementObjectSearcher(
                    "root/wmi",
                    "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                var lines = new List<string>();
                using var results = searcher.Get();
                foreach (var zone in results.OfType<ManagementObject>())
                {
                    var celsius = (zone["CurrentTemperature"] as double? ?? Convert.ToDouble(zone["CurrentTemperature"])) / 10.0 - 273.15;
                    lines.Add($"{zone["InstanceName"]}: {celsius:F1} C");
                    zone.Dispose();
                }

                return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "(датчики температуры не найдены)";
            }, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "(датчик температуры недоступен)";
        }
    }

    /// <summary>Устаревшее имя. Используйте <see cref="GetThermalZoneTemperatureAsync"/>.</summary>
    public Task<string> GetCpuTemperatureAsync(CancellationToken ct = default)
        => GetThermalZoneTemperatureAsync(ct);

    // ===================== Память и файловая система =====================

    private static IReadOnlyList<RegistryTweak> ShortNames8dot3Tweak =>
    [
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\FileSystem",
            "NtfsDisable8dot3NameCreation",
            RegistryValueKind.DWord,
            1, 0)
    ];

    private static IReadOnlyList<RegistryTweak> LastAccessTweak =>
    [
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\FileSystem",
            "NtfsDisableLastAccessUpdate",
            RegistryValueKind.DWord,
            1, 0)
    ];

    private static IReadOnlyList<RegistryTweak> PrefetcherTweak =>
    [
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
            "EnablePrefetcher",
            RegistryValueKind.DWord,
            0, 3)
    ];

    // Сжатие памяти — состояние читается только через Get-MMAgent (в реестре его нет).
    // Ошибка чтения → Failure (Unknown в UI), а не false/OFF.
    public async Task<Result<bool>> GetMemoryCompressionAsync(CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NoLogo", "-Command", "(Get-MMAgent).MemoryCompression"],
                null,
                ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result<bool>.Failure("Не удалось прочитать Memory Compression (код " + result.Code + ").", result.Code);
        }

        var value = (result.Value ?? string.Empty).Trim();
        if (value.Equals("True", StringComparison.OrdinalIgnoreCase))
            return Result<bool>.Success(true);
        if (value.Equals("False", StringComparison.OrdinalIgnoreCase))
            return Result<bool>.Success(false);

        // Нераспознанный вывод — Unknown, не false.
        return Result<bool>.Failure("Не удалось распознать состояние Memory Compression: «" + value + "».");
    }

    public async Task<Result> SetMemoryCompressionAsync(bool enable, CancellationToken ct = default)
    {
        var command = enable ? "Enable-MMAgent -mc" : "Disable-MMAgent -mc";
        var result = await _runner
            .RunAsync("powershell.exe", ["-NoProfile", "-NoLogo", "-Command", command], null, ct)
            .ConfigureAwait(false);
        _logger.Info($"POWER | memory compression={enable} | rc={result.Code}");
        return result.IsSuccess
            ? Result.Success(enable ? "Сжатие памяти включено." : "Сжатие памяти отключено.")
            : Result.Failure("Не удалось переключить сжатие памяти (код " + result.Code + ").", result.Code);
    }

    public Result Set8dot3NamesEnabled(bool enable)
    {
        var tweak = ShortNames8dot3Tweak[0] with { OffValue = enable ? 0 : 1 };
        var result = _registry.Apply([tweak], BackupPath("8dot3names.json"));
        return result.IsSuccess
            ? Result.Success(enable
                ? "Создание имён 8.3 включено (перезагрузка не требуется для новых файлов)."
                : "Создание имён 8.3 отключено (полностью применится после перезагрузки).")
            : result;
    }

    public Result SetLastAccessEnabled(bool enable)
    {
        var tweak = LastAccessTweak[0] with { OffValue = enable ? 0 : 1 };
        var result = _registry.Apply([tweak], BackupPath("lastaccess.json"));
        if (!result.IsSuccess)
            return result;

        // Повторное чтение: Success только если значение реально совпало с ожидаемым.
        var info = GetLastAccessInfo();
        var expectedDisabled = !enable;
        var actualDisabled = info.State == SystemSettingState.Disabled;
        if (info.State is SystemSettingState.Unknown)
        {
            return Result.Failure("Параметр записан, но состояние Last Access не удалось подтвердить чтением.");
        }

        if (actualDisabled != expectedDisabled)
        {
            return Result.Failure(
                "Параметр Last Access записан, но effective state не совпал с ожидаемым. " +
                "Возможно, требуется перезагрузка или права администратора.");
        }

        return Result.Success(enable
            ? "Учёт времени последнего доступа включён."
            : "Учёт времени последнего доступа отключён.");
    }

    public Result SetPrefetcherEnabled(bool enable)
    {
        var tweak = PrefetcherTweak[0] with { OffValue = enable ? 3 : 0 };
        var result = _registry.Apply([tweak], BackupPath("prefetcher.json"));
        return result.IsSuccess
            ? Result.Success(enable ? "Prefetcher включён." : "Prefetcher отключён.")
            : result;
    }

    /// <summary>
    /// Полная модель 8.3: глобальный режим 0/1/2/3 и effective state.
    /// Не сводит mode 2/3 к простому bool.
    /// </summary>
    public static ShortNamesInfo GetShortNamesInfo()
    {
        int? raw;
        try
        {
            raw = ReadFileSystemDWord("NtfsDisable8dot3NameCreation");
        }
        catch
        {
            return new ShortNamesInfo(SystemSettingState.Unknown, ShortNameGlobalMode.Unknown, null);
        }

        if (raw is null)
        {
            // По умолчанию Windows: зависит от версии; на современных клиентах часто 2 (per-volume).
            return new ShortNamesInfo(SystemSettingState.Unknown, ShortNameGlobalMode.Unknown, null);
        }

        var mode = MapShortNameGlobalMode(raw.Value);
        var effective = MapShortNameEffective(mode);
        return new ShortNamesInfo(effective, mode, VolumeStates: null);
    }

    /// <summary>
    /// Как GetShortNamesInfo, но для PerVolume/DisabledExceptSystem опрашивает тома через fsutil 8dot3name query.
    /// </summary>
    public async Task<Result<ShortNamesInfo>> GetShortNamesInfoAsync(CancellationToken ct = default)
    {
        var baseInfo = GetShortNamesInfo();
        if (baseInfo.GlobalMode is not (ShortNameGlobalMode.PerVolume or ShortNameGlobalMode.DisabledExceptSystem))
        {
            return Result<ShortNamesInfo>.Success(baseInfo);
        }

        try
        {
            var volumes = new List<(string Volume, bool Enabled)>();
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                ct.ThrowIfCancellationRequested();
                var root = drive.Name.TrimEnd('\\');
                var query = await _runner
                    .RunAsync("fsutil", ["8dot3name", "query", root], null, ct)
                    .ConfigureAwait(false);
                if (!query.IsSuccess || query.Value is null)
                    continue;

                var text = query.Value;
                var enabled = true;
                // "The volume state is: 0 (enabled)" / "1 (disabled)" — ищем цифру после state.
                var markers = new[] { "volume state is:", "состояние тома:" };
                var found = false;
                foreach (var marker in markers)
                {
                    var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    var tail = text[(idx + marker.Length)..];
                    var digit = tail.SkipWhile(c => !char.IsDigit(c)).FirstOrDefault();
                    if (digit == '0') { enabled = true; found = true; break; }
                    if (digit == '1') { enabled = false; found = true; break; }
                }

                if (!found)
                {
                    enabled = text.Contains("enabled", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains("disabled", StringComparison.OrdinalIgnoreCase);
                }

                volumes.Add((root + "\\", enabled));
            }

            var effective = baseInfo.EffectiveState;
            if (volumes.Count > 0)
            {
                var allOn = volumes.All(v => v.Enabled);
                var allOff = volumes.All(v => !v.Enabled);
                effective = allOn ? SystemSettingState.Enabled
                    : allOff ? SystemSettingState.Disabled
                    : SystemSettingState.Mixed;
            }

            return Result<ShortNamesInfo>.Success(new ShortNamesInfo(
                effective,
                baseInfo.GlobalMode,
                volumes.Count > 0 ? volumes : null));
        }
        catch (OperationCanceledException)
        {
            return Result<ShortNamesInfo>.Failure("Отменено", -1);
        }
        catch
        {
            return Result<ShortNamesInfo>.Success(baseInfo);
        }
    }

    internal static ShortNameGlobalMode MapShortNameGlobalMode(int raw) =>
        raw switch
        {
            0 => ShortNameGlobalMode.EnabledForAll,
            1 => ShortNameGlobalMode.DisabledForAll,
            2 => ShortNameGlobalMode.PerVolume,
            3 => ShortNameGlobalMode.DisabledExceptSystem,
            _ => ShortNameGlobalMode.Unknown
        };

    internal static SystemSettingState MapShortNameEffective(ShortNameGlobalMode mode) =>
        mode switch
        {
            ShortNameGlobalMode.EnabledForAll => SystemSettingState.Enabled,
            ShortNameGlobalMode.DisabledForAll => SystemSettingState.Disabled,
            ShortNameGlobalMode.PerVolume => SystemSettingState.Mixed,
            ShortNameGlobalMode.DisabledExceptSystem => SystemSettingState.PartiallyEnabled,
            _ => SystemSettingState.Unknown
        };

    /// <summary>Совместимость: true только при однозначно «включено для всех» (mode 0).</summary>
    public static bool Is8dot3Enabled()
    {
        var info = GetShortNamesInfo();
        return info.GlobalMode == ShortNameGlobalMode.EnabledForAll;
    }

    /// <summary>
    /// Last Access: NtfsDisableLastAccessUpdate (и при возможности fsutil).
    /// Значения (Microsoft): 0 = user, updates on; 1 = user, updates off;
    /// 2 = system managed, updates on; 3 = system managed, updates off.
    /// Младший бит 1 → updates disabled. Не сводим ошибку к false.
    /// </summary>
    public static LastAccessInfo GetLastAccessInfo()
    {
        int? value;
        try
        {
            value = ReadFileSystemDWord("NtfsDisableLastAccessUpdate");
        }
        catch
        {
            return new LastAccessInfo(SystemSettingState.Unknown, null, RequiresReboot: false);
        }

        if (value is null)
            return new LastAccessInfo(SystemSettingState.Unknown, null, RequiresReboot: false);

        // Младший бит: 0 = last access updates enabled, 1 = disabled.
        var disabled = (value.Value & 1) != 0;
        // На современных Windows изменение обычно применяется без reboot;
        // RequiresReboot оставляем false при чистом чтении. После Set* UI
        // может выставить PendingReboot, если нужно подчеркнуть осторожность.
        return new LastAccessInfo(
            disabled ? SystemSettingState.Disabled : SystemSettingState.Enabled,
            value,
            RequiresReboot: false);
    }

    /// <summary>
    /// Дополняет GetLastAccessInfo опросом <c>fsutil behavior query disablelastaccess</c>.
    /// Если registry и fsutil расходятся — PendingReboot / Mixed.
    /// </summary>
    public async Task<Result<LastAccessInfo>> GetLastAccessInfoAsync(CancellationToken ct = default)
    {
        var baseInfo = GetLastAccessInfo();
        try
        {
            var fsutil = await _runner
                .RunAsync("fsutil", ["behavior", "query", "disablelastaccess"], null, ct)
                .ConfigureAwait(false);

            if (!fsutil.IsSuccess || string.IsNullOrWhiteSpace(fsutil.Value))
            {
                // Registry-only уже есть; fsutil недоступен — не превращаем в Unknown.
                return Result<LastAccessInfo>.Success(baseInfo);
            }

            var text = fsutil.Value;
            // Типичный вывод: "DisableLastAccess = 1" или локализованный аналог.
            int? fsutilValue = null;
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var rhs = line[(eq + 1)..].Trim();
                if (int.TryParse(rhs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    fsutilValue = n;
                    break;
                }
            }

            if (fsutilValue is null)
                return Result<LastAccessInfo>.Success(baseInfo);

            var regDisabled = baseInfo.RawValue is int rv && (rv & 1) != 0;
            var fsDisabled = (fsutilValue.Value & 1) != 0;

            if (baseInfo.State == SystemSettingState.Unknown)
            {
                return Result<LastAccessInfo>.Success(new LastAccessInfo(
                    fsDisabled ? SystemSettingState.Disabled : SystemSettingState.Enabled,
                    fsutilValue,
                    RequiresReboot: false));
            }

            if (regDisabled != fsDisabled)
            {
                // Реестр уже новый, runtime (fsutil) ещё старый — типичный PendingReboot.
                return Result<LastAccessInfo>.Success(new LastAccessInfo(
                    SystemSettingState.PendingReboot,
                    baseInfo.RawValue ?? fsutilValue,
                    RequiresReboot: true));
            }

            return Result<LastAccessInfo>.Success(baseInfo);
        }
        catch (OperationCanceledException)
        {
            return Result<LastAccessInfo>.Failure("Отменено", -1);
        }
        catch
        {
            return Result<LastAccessInfo>.Success(baseInfo);
        }
    }

    public static bool IsLastAccessEnabled()
    {
        var info = GetLastAccessInfo();
        return info.State == SystemSettingState.Enabled;
    }

    public static bool? TryIsPrefetcherEnabled()
    {
        var value = ReadPrefetcherDWord("EnablePrefetcher");
        if (value is null) return null;
        return value != 0;
    }

    public static bool IsPrefetcherEnabled() => TryIsPrefetcherEnabled() ?? true;

    public static bool? TryIsSysMainEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SysMain");
            return key?.GetValue("Start") switch
            {
                int start => start != 4,
                null => null,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public static bool IsSysMainEnabled() => TryIsSysMainEnabled() ?? true;

    private static int? ReadFileSystemDWord(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue(valueName) as int?;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadPrefetcherDWord(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            return key?.GetValue(valueName) as int?;
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsBcdValue(string output, string valueName)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(valueName + " ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BackupPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "power", fileName);

    // Соответствие «GUID шаблона -> GUID созданной копии» (plans.json). Кэшируется на запуск;
    // повреждённый файл молча даёт пустой словарь — тогда просто создастся новая копия.
    private Dictionary<string, string>? _duplicatePlans;

    private static string? GetStableDuplicateGuid(string templateGuid)
    {
        if (string.Equals(templateGuid, UltimatePerformancePlanGuid, StringComparison.OrdinalIgnoreCase))
        {
            return UltimatePerformanceCopyGuid;
        }

        if (string.Equals(templateGuid, BitsumHighestPerformancePlanGuid, StringComparison.OrdinalIgnoreCase))
        {
            return BitsumHighestPerformanceCopyGuid;
        }

        return null;
    }

    private Dictionary<string, string> LoadDuplicatePlans()
    {
        if (_duplicatePlans is not null)
        {
            return _duplicatePlans;
        }

        try
        {
            var path = BackupPath("plans.json");
            _duplicatePlans = File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch
        {
            _duplicatePlans = [];
        }

        return _duplicatePlans;
    }

    private void SaveDuplicatePlans(Dictionary<string, string> plans)
    {
        try
        {
            var path = BackupPath("plans.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(plans));
            _duplicatePlans = plans;
        }
        catch (Exception exception)
        {
            _logger.Warn("POWER | plans.json save failed | " + exception.Message);
        }
    }
}
