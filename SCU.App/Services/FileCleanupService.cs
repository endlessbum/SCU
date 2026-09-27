using SCU.Common;

namespace SCU.Services;

// Цель очистки: каталог, маска файлов и признак удаления вложенных каталогов.
public sealed record CleanupTarget(string Label, string Directory, string Mask = "*", bool RemoveSubdirectories = true);

// Итог по одной цели: сколько файлов было, сколько удалено, сколько осталось (занятые).
public sealed record CleanupItemResult(string Label, bool Existed, long FilesBefore, long FilesDeleted, long BytesDeleted);

// Файловая очистка — аналог :CleanDirContents из Utilities.bat:
// занятые файлы пропускаются, итог сверяется повторным подсчётом.
public sealed class FileCleanupService
{
    private readonly Logger _logger;

    public FileCleanupService(Logger logger)
    {
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<CleanupItemResult>>> CleanAsync(
        IReadOnlyList<CleanupTarget> targets,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        try
        {
            var results = await Task.Run(
                () => CleanAll(targets, progress, ct),
                ct).ConfigureAwait(false);
            return Result<IReadOnlyList<CleanupItemResult>>.Success(results);
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyList<CleanupItemResult>>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<CleanupItemResult>>.Failure(exception.Message);
        }
    }

    public static IReadOnlyList<CleanupTarget> GetTempTargets()
    {
        var targets = new List<CleanupTarget>
        {
            new("Windows\\Temp", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")),
            new("Prefetch", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch"), "*.pf"),
            new(
                "Recent",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent")),
            new(
                "INetCache",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "INetCache"))
        };

        // %TEMP% может быть перенаправлен (RAM-диск, нестандартная папка): чистим его
        // только если путь действительно похож на temp-каталог, иначе — канонический
        // %LOCALAPPDATA%\Temp. Без проверки рекурсивно удалялось бы содержимое
        // произвольной папки, на которую кто-то указал %TEMP%.
        var userTemp = Environment.GetEnvironmentVariable("TEMP");
        var tempPath = IsLikelyTempDirectory(userTemp)
            ? userTemp!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        targets.Insert(0, new CleanupTarget("TEMP пользователя", tempPath));
        return targets;
    }

    // «Похоже на temp»: лист каталога — Temp/Tmp и путь внутри профиля пользователя.
    internal static bool IsLikelyTempDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(full);
            if (!leaf.Equals("Temp", StringComparison.OrdinalIgnoreCase)
                && !leaf.Equals("Tmp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Граница пути через GetRelativePath, а не StartsWith: префиксное
            // сравнение пропускает соседние каталоги (C:\Users\Alex2 vs Alex).
            return PathSafety.IsUnderDirectoryOrEqual(full, profile)
                || PathSafety.IsUnderDirectoryOrEqual(full, localAppData);
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<CleanupTarget> GetBrowserCacheTargets()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var chromiumUserDirs = new[]
        {
            "Google\\Chrome\\User Data",
            "Microsoft\\Edge\\User Data",
            "Yandex\\YandexBrowser\\User Data",
            "BraveSoftware\\Brave-Browser\\User Data",
            "Vivaldi\\User Data",
            "Chromium\\User Data"
        };
        var chromiumCacheDirs = new[] { "Cache", "Code Cache", "GPUCache", "Service Worker\\CacheStorage" };

        var targets = new List<CleanupTarget>();

        foreach (var userDir in chromiumUserDirs)
        {
            var root = Path.Combine(local, userDir);
            if (!Directory.Exists(root))
            {
                continue;
            }

            var browserName = userDir.Split('\\')[0];
            foreach (var profile in Directory.EnumerateDirectories(root))
            {
                foreach (var cacheDir in chromiumCacheDirs)
                {
                    targets.Add(new CleanupTarget($"{browserName}: {Path.GetFileName(profile)} {cacheDir.Replace('\\', '/')}", Path.Combine(profile, cacheDir)));
                }
            }
        }

        var firefoxRoot = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxRoot))
        {
            foreach (var profile in Directory.EnumerateDirectories(firefoxRoot))
            {
                foreach (var cacheDir in new[] { "cache2", "startupCache", "OfflineCache" })
                {
                    targets.Add(new CleanupTarget($"Firefox: {Path.GetFileName(profile)} {cacheDir}", Path.Combine(profile, cacheDir)));
                }
            }
        }

        foreach (var profile in new[] { "Opera Stable", "Opera GX Stable" })
        {
            var root = Path.Combine(roaming, "Opera Software", profile);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var cacheDir in new[] { "Cache", "GPUCache", "Code Cache" })
            {
                targets.Add(new CleanupTarget($"Opera ({profile}): {cacheDir}", Path.Combine(root, cacheDir)));
            }
        }

        return targets;
    }

    public static IReadOnlyList<CleanupTarget> GetUpdateCacheTargets() =>
    [
        new CleanupTarget(
            "кэш загрузок обновлений",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download")),
        new CleanupTarget(
            "DO: NetworkService cache",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")),
        new CleanupTarget(
            "DO: ProgramData cache",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "DeliveryOptimization", "Cache"))
    ];

    // Объём файлов, доступных для очистки, по целям (сводка для карточек раздела).
    // Занятые и нечитаемые файлы посчитать нельзя — они были бы пропущены и при очистке.
    public static long MeasureBytes(IReadOnlyList<CleanupTarget> targets)
    {
        long total = 0;
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Directory) || !Directory.Exists(target.Directory))
            {
                continue;
            }

            foreach (var file in EnumerateFilesSafe(target.Directory, target.Mask))
            {
                total += file.Length;
            }
        }

        return total;
    }

    private List<CleanupItemResult> CleanAll(
        IReadOnlyList<CleanupTarget> targets,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var results = new List<CleanupItemResult>(targets.Count);

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(target.Directory) || !Directory.Exists(target.Directory))
            {
                _logger.Info($"CLEAN | {target.Label} | нет папки");
                continue;
            }

            progress?.Report($"Очистка: {target.Label}…");
            var before = CountFiles(target.Directory, target.Mask);
            var deletedFiles = 0;
            var deletedBytes = 0L;

            foreach (var file in EnumerateFilesSafe(target.Directory, target.Mask))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var length = file.Length;
                    file.Delete();
                    deletedFiles++;
                    deletedBytes += length;
                }
                catch (Exception)
                {
                    // Занятый или защищённый файл — пропускаем, как del /f /s /q в BAT.
                }
            }

            if (target.RemoveSubdirectories)
            {
                RemoveSubdirectories(target.Directory, ct);
            }

            var after = CountFiles(target.Directory, target.Mask);
            results.Add(new CleanupItemResult(target.Label, true, before ?? 0, deletedFiles, deletedBytes));

            // null = подсчёт не удался (каталог нечитаем): не маскируем под «уже пусто».
            var message = before is null
                ? $"{target.Label}: каталог нечитаем — подсчитать файлы не удалось"
                : before == 0
                    ? $"{target.Label}: уже пусто"
                    : after == 0
                        ? $"{target.Label}: удалено файлов {before}"
                        : $"{target.Label}: удалено {deletedFiles}, осталось {after} (занятые пропущены)";
            progress?.Report(message);
            _logger.Info($"CLEAN | {target.Label} | before={before?.ToString() ?? "error"} after={after?.ToString() ?? "error"} deleted={deletedFiles} bytes={deletedBytes}");
        }

        return results;
    }

    // null — подсчёт не удался (каталог нечитаем); 0 — каталог действительно пуст.
    // IgnoreInaccessible у EnumerateFilesSafe проглатывает отказ в доступе, поэтому
    // нулевой результат перепроверяется прямой пробой читаемости каталога.
    private static long? CountFiles(string directory, string mask)
    {
        try
        {
            var count = EnumerateFilesSafe(directory, mask).LongCount();
            return count == 0 && !IsDirectoryReadable(directory) ? null : count;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsDirectoryReadable(string directory)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(directory).Length;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<FileInfo> EnumerateFilesSafe(string directory, string mask)
    {
        var options = new EnumerationOptions
        {
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = true,
            RecurseSubdirectories = true
        };

        var directoryInfo = new DirectoryInfo(directory);
        if (mask != "*")
        {
            return directoryInfo.EnumerateFiles(mask, options);
        }

        // Маска "*" в BAT ловит и файлы без расширения — берём всё.
        return directoryInfo.EnumerateFiles("*", options);
    }

    private void RemoveSubdirectories(string directory, CancellationToken ct)
    {
        try
        {
            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Directory.Delete(subdirectory, recursive: true);
                }
                catch (Exception)
                {
                    // Занятый каталог — пропускаем, как rd /s /q в BAT.
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Warn("CLEAN | перечисление подкаталогов прервано: " + exception.Message);
        }
    }
}
