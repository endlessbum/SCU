using System.Globalization;
using System.Management;
using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.Infrastructure.Windows.Power;

// Файл подкачки и crash dump (п. 13 аудита: зона ответственности).
public sealed partial class PowerService
{
    // Файл подкачки: отключение автозатем управления и фиксация размера (аналог :PFApply).
    // Изменения применяются после перезагрузки. Прежние настройки сохраняются в бэкап,
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

    // Бэкап перед КАЖДЫМ деструктивным изменением pagefile (не «один раз навсегда»).
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
                return Result.Failure("Бэкап файла подкачки не был создан.");
            }

            _logger.Info("PAGEFILE | backup -> " + backupFile);
            return Result.Success("Бэкап сохранён: " + Path.GetFileName(backupFile));
        }
        catch (Exception exception)
        {
            _logger.Error("PAGEFILE | backup failed | " + exception);
            return Result.Failure("Не удалось сохранить бэкап файла подкачки: " + exception.Message);
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
}
