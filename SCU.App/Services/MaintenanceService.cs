using System.Management;
using SCU.Common;
using SCU.Interop;

namespace SCU.Services;

// Раздел 12 «Поиск и целостность» — аналог :MaintMenu из Utilities.bat.
// Индексация: attrib +I на выбранных дисках (рекурсивно) с проверкой атрибута корня.
// Целостность: DISM /RestoreHealth, затем sfc /scannow — долгие операции с отменой.
public sealed class MaintenanceService
{
    private readonly Logger _logger;
    private readonly LongProcessRunner _runner;

    public MaintenanceService(Logger logger, LongProcessRunner runner)
    {
        _logger = logger;
        _runner = runner;
    }

    // Список локальных дисков (wmic logicaldisk в BAT → Win32_LogicalDisk).
    // Токен проверяется внутри делегата: pre-cancelled даёт OCE в VM (контракта Result нет),
    // а не TaskCanceledException от второго аргумента Task.Run.
    public Task<IReadOnlyList<string>> GetDrivesAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var drives = new List<string>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, DriveType FROM Win32_LogicalDisk WHERE DriveType = 3");
        using var results = searcher.Get();
        foreach (var disk in results.OfType<ManagementObject>())
        {
            using (disk)
            {
                if (disk["DeviceID"] is string id && id.Length >= 2)
                {
                    drives.Add(id[..2]);
                }
            }
        }

        return (IReadOnlyList<string>)drives.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }, CancellationToken.None);

    // +I «не индексировать» на диск: корень ставится напрямую через .NET, содержимое
    // рекурсивно обходится (аналог attrib +I X: /S /D из BAT — attrib сам по себе корень
    // диска не адресует: и «C:», и «C:\» дают «Не найден файл», проверено на Win11).
    public async Task<Result> DisableIndexingAsync(IReadOnlyList<string> drives, CancellationToken ct = default)
    {
        if (drives.Count == 0)
        {
            return Result.Failure("Диски не выбраны.");
        }

        var failures = new List<string>();
        foreach (var drive in drives)
        {
            ct.ThrowIfCancellationRequested();
            var root = drive.EndsWith('\\') ? drive : drive + "\\";
            try
            {
                var result = await Task.Run(() => DisableIndexingRecursive(root, ct), ct).ConfigureAwait(false);
                if (VerifyNotIndexed(drive))
                {
                    _logger.Info($"MAINT | indexing | {drive} | disabled (verified) | skipped={result}");
                }
                else
                {
                    failures.Add(drive);
                    _logger.Warn($"MAINT | indexing | {drive} | verify failed");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(drive);
                _logger.Warn($"MAINT | indexing | {drive} | ошибка: {exception.Message}");
            }
        }

        return failures.Count == 0
            ? Result.Success("Индексация содержимого отключена на выбранных дисках (проверено чтением атрибутов).")
            : Result.Failure("Не все диски удалось обработать: " + string.Join(", ", failures));
    }

    // Рекурсивный обход диска: атрибут «не индексировать» на каталоги и файлы.
    // Занятые и защищённые объекты пропускаются — как у attrib /S /D.
    private static int DisableIndexingRecursive(string root, CancellationToken ct)
    {
        var skipped = 0;
        var options = new System.IO.EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = 0
        };

        var rootInfo = new DirectoryInfo(root);
        if (!SetNotIndexed(rootInfo))
        {
            skipped++;
        }

        foreach (var directory in rootInfo.EnumerateDirectories("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!SetNotIndexed(directory))
            {
                skipped++;
            }
        }

        foreach (var file in rootInfo.EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!SetNotIndexed(file))
            {
                skipped++;
            }
        }

        return skipped;
    }

    private static bool SetNotIndexed(FileSystemInfo target)
    {
        try
        {
            if ((target.Attributes & FileAttributes.NotContentIndexed) != 0)
            {
                return true;
            }

            target.Attributes |= FileAttributes.NotContentIndexed;
            return (target.Attributes & FileAttributes.NotContentIndexed) != 0;
        }
        catch
        {
            // Занятый или защищённый объект — пропускаем.
            return false;
        }
    }

    // DISM /Online /Cleanup-Image /RestoreHealth, затем sfc /scannow — итог с кодами обоих.
    // После «Отмены» SFC не запускается.
    public async Task<Result> RunIntegrityCheckAsync(CancellationToken ct = default)
    {
        var dism = await _runner
            .RunAsync("DISM.exe", ["/Online", "/Cleanup-Image", "/RestoreHealth"], null, ct)
            .ConfigureAwait(false);
        _logger.Info("MAINT | DISM | rc=" + dism.Code);

        if (ct.IsCancellationRequested)
        {
            _logger.Warn("MAINT | отменено после DISM — SFC не запускается");
            return Result.Failure("Отменено", -1);
        }

        var sfc = await _runner
            .RunAsync("sfc.exe", ["/scannow"], null, ct)
            .ConfigureAwait(false);
        _logger.Info("MAINT | SFC | rc=" + sfc.Code);

        if (dism.IsSuccess && sfc.IsSuccess)
        {
            return Result.Success("DISM и SFC выполнены успешно (оба кода 0).");
        }

        var problems = new List<string>();
        if (!dism.IsSuccess)
        {
            problems.Add($"DISM — код {dism.Code}");
        }

        if (!sfc.IsSuccess)
        {
            problems.Add($"SFC — код {sfc.Code}");
        }

        return Result.Failure("Проверка целостности завершилась с замечаниями: " + string.Join("; ", problems));
    }

    // ===================== Глубокая очистка (WinSxS, DO-кэш, CompactOS) =====================

    // Анализ хранилища компонентов: размер WinSxS и рекомендация очистки.
    // Возвращаем сырой вывод DISM — локализованный текст показывается пользователю.
    public async Task<Result<string>> AnalyzeComponentStoreAsync(CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync("DISM.exe", ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore"], null, ct)
            .ConfigureAwait(false);
        _logger.Info("MAINT | DISM analyze | rc=" + result.Code);
        return result.IsSuccess
            ? Result<string>.Success((result.Value ?? string.Empty).Trim())
            : Result<string>.Failure("DISM завершился с кодом " + result.Code, result.Code);
    }

    // Очистка хранилища компонентов. resetBase=true (/ResetBase) сжимает хранилище сильнее,
    // но установленные обновления становится невозможно удалить — решение за пользователем.
    public async Task<Result> StartComponentCleanupAsync(bool resetBase, CancellationToken ct = default)
    {
        string[] arguments = resetBase
            ? ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"]
            : ["/Online", "/Cleanup-Image", "/StartComponentCleanup"];
        var result = await _runner.RunAsync("DISM.exe", arguments, null, ct).ConfigureAwait(false);
        _logger.Info($"MAINT | StartComponentCleanup resetBase={resetBase} | rc={result.Code}");
        return result.IsSuccess
            ? Result.Success(resetBase
                ? "Хранилище компонентов очищено (/ResetBase). Установленные обновления больше нельзя удалить."
                : "Хранилище компонентов очищено.")
            : Result.Failure("DISM завершился с кодом " + result.Code, result.Code);
    }

    // Кэш Delivery Optimization (P2P-раздача обновлений) — штатный cmdlet.
    public async Task<Result> ClearDeliveryOptimizationCacheAsync(CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NoLogo", "-Command", "Delete-DeliveryOptimizationCache -Force"],
                null,
                ct)
            .ConfigureAwait(false);
        _logger.Info("MAINT | DO cache | rc=" + result.Code);
        return result.IsSuccess
            ? Result.Success("Кэш Delivery Optimization очищен.")
            : Result.Failure("Не удалось очистить кэш Delivery Optimization (код " + result.Code + ").", result.Code);
    }

    // CompactOS: состояние сжатия системных файлов. compact /compactos:query выводит
    // локализованный текст («находится в состоянии сжатия» / «не находится…»,
    // COMPRESSION_STATE_ALWAYS/NONE) и завершается с кодом 102, когда сжатие выключено,
    // поэтому запрос идёт через powershell с безусловным exit 0 — иначе раннер теряет вывод.
    public async Task<Result<bool>> GetCompactOsEnabledAsync(CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NoLogo", "-Command", "compact.exe /compactos:query 2>&1 | Out-String; exit 0"],
                null,
                ct)
            .ConfigureAwait(false);
        var output = result.Value ?? string.Empty;
        if (output.Length == 0)
        {
            return Result<bool>.Failure("compact не вернул вывод (код " + result.Code + ").");
        }

        // Порядок важен: флаг-подсказка в выводе обозначает ПРОТИВОПОЛОЖНОЕ состояние
        // («может быть включён с помощью /compactos:always» = сейчас выключено).
        // На английских сборках подсказка содержит ALWAYS/NEVER — раньше матчилось
        // как признак текущего состояния и инвертировало результат.
        if (output.Contains("/compactos:never", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Success(true);
        }

        if (output.Contains("/compactos:always", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Success(false);
        }

        if (output.Contains("COMPRESSION_STATE_ALWAYS", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Success(true);
        }

        if (output.Contains("COMPRESSION_STATE_NONE", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Success(false);
        }

        // Windows может вернуть обычный локализованный текст вместо enum-маркеров.
        // Сначала проверяем отрицательную форму, потому что она содержит «in compact state».
        if (output.Contains("NOT in compact state", StringComparison.OrdinalIgnoreCase)
            || output.Contains("не находится в состоянии сжатия", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Success(false);
        }

        if (output.Contains("in compact state", StringComparison.OrdinalIgnoreCase)
            || output.Contains("находится в состоянии сжатия", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Success(true);
        }

        // Русская локализация может вообще не содержать подсказки — проверяем более общие фразы.
        if (output.Contains("отключено", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Success(false);
        }

        if (output.Contains("включено", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Success(true);
        }

        return Result<bool>.Failure("Не удалось определить состояние CompactOS из вывода compact.");
    }

    public async Task<Result> SetCompactOsAsync(bool enable, CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync("compact.exe", ["/compactos:" + (enable ? "always" : "never")], null, ct)
            .ConfigureAwait(false);
        _logger.Info($"MAINT | compactos={enable} | rc={result.Code}");
        return result.IsSuccess
            ? Result.Success(enable
                ? "CompactOS включён: системные файлы сжаты."
                : "CompactOS отключён: системные файлы распакованы.")
            : Result.Failure("compact завершился с кодом " + result.Code, result.Code);
    }

    // Цели «служебной» очистки: дампы памяти, очередь WER, отчёты ошибок.
    // Занятые файлы FileCleanupService пропускает — операция безопасна в любой момент.
    public static IReadOnlyList<CleanupTarget> GetDeepCleanTargets()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var wer = Path.Combine(programData, "Microsoft", "Windows", "WER");

        return
        [
            new CleanupTarget("Дамп памяти (MEMORY.DMP)", windows, "MEMORY.DMP", RemoveSubdirectories: false),
            new CleanupTarget("Minidump", Path.Combine(windows, "Minidump")),
            new CleanupTarget("LiveKernelReports", Path.Combine(windows, "LiveKernelReports")),
            new CleanupTarget("WER Temp", Path.Combine(wer, "Temp")),
            new CleanupTarget("WER ReportQueue", Path.Combine(wer, "ReportQueue")),
            new CleanupTarget("WER ReportArchive", Path.Combine(wer, "ReportArchive"))
        ];
    }

    private static bool VerifyNotIndexed(string drive)
    {
        try
        {
            var root = drive.EndsWith('\\') ? drive : drive + "\\";
            var attributes = File.GetAttributes(root);
            return (attributes & FileAttributes.NotContentIndexed) != 0;
        }
        catch
        {
            return false;
        }
    }
}
