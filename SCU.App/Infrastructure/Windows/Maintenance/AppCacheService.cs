using System.Diagnostics;
using SCU.Common;

namespace SCU.Infrastructure.Windows.Maintenance;

// Кэш одного приложения: прямая подпапка профиля (Local/Roaming/LocalLow) и
// найденные в ней каталоги временных данных. Замер и очистка идут только по
// этим каталогам — профили, конфиги и пользовательские данные не трогаются.
public sealed record AppCacheInfo(string AppName, string FolderPath, IReadOnlyList<CleanupTarget> Targets, long TotalBytes);

// Поиск и замер кэша установленных приложений (раздел 26 «Кэш приложений»).
// «Временные данные» — подкаталоги с известными кэш-именами (Cache, Code Cache,
// GPUCache, cache2, Temp и т.п.) на глубине до MaxCacheDepth от папки
// приложения. Имена берутся из наблюдаемой практики (Chromium, Firefox, Electron,
// Opera и общий случай «папка Temp внутри AppData»), а не из реестра —
// деинсталляционных записей для кэша не существует.
public sealed class AppCacheService
{
    // Максимальная глубина кэш-каталога от корня папки приложения: 4 покрывает
    // практический случай Chromium (Google\Chrome\User Data\<profile>\Code Cache —
    // глубина 4), глубже лежат данные, а не временные файлы.
    private const int MaxCacheDepth = 4;

    internal static readonly IReadOnlyList<string> DefaultCacheDirectoryNames =
    [
        "Cache", "Code Cache", "CacheStorage", "GPUCache", "DawnCache",
        "ShaderCache", "D3DSCache", "GrShaderCache", "cache2", "startupCache",
        "OfflineCache", "Temp", "Tmp", "CrashReports", "CrashDumps",
    ];

    // Прямые дети корней профиля, которые приложением не считаются:
    // системные каталоги и сам SCU. Список повторяет защиту
    // InstalledAppsService.RemoveResidualFolders (плюс SCU).
    internal static readonly IReadOnlyList<string> ExcludedAppFolderNames =
    [
        "Microsoft", "Windows", "Common Files", "ProgramData", "Program Files",
        "Program Files (x86)", "Users", "Temp", "Packages", "Desktop",
        "Documents", "SCU",
    ];

    private readonly Logger _logger;
    private readonly IReadOnlyList<string> _roots;
    private readonly IReadOnlyList<string> _cacheDirectoryNames;

    public AppCacheService(
        Logger logger,
        IReadOnlyList<string>? roots = null,
        IReadOnlyList<string>? cacheDirectoryNames = null)
    {
        _logger = logger;
        _roots = roots ?? DefaultRoots();
        _cacheDirectoryNames = cacheDirectoryNames ?? DefaultCacheDirectoryNames;
    }

    private static IReadOnlyList<string> DefaultRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(profile, "AppData", "LocalLow"),
        ];
    }

    // Скан профиля: приложения — прямые дети корней; кэш — временные каталоги
    // внутри. Одно имя приложения может встретиться в нескольких корнях
    // (Google в Local и Roaming) — карточка одна, цели объединяются.
    public async Task<IReadOnlyList<AppCacheInfo>> ScanAsync(CancellationToken ct = default) =>
        await Task.Run(() => Scan(ct), ct).ConfigureAwait(false);

    public IReadOnlyList<AppCacheInfo> Scan(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // Кандидаты: (имя приложения, путь к папке).
        var candidates = new List<(string Name, string Path)>();
        foreach (var root in _roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(directory);
                    if (!IsExcludedAppName(name))
                    {
                        candidates.Add((name, directory));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Warn("APPCACHE | root unreadable | " + root + " | " + exception.Message);
            }
        }

        // Замер — тяжёлая часть: параллелим по приложениям (паттерн
        // AppsViewModel.StartSizeComputation), результаты группируем по имени.
        var scanned = candidates
            .AsParallel()
            .WithCancellation(ct)
            .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
            .Select(candidate => MeasureApp(candidate.Name, candidate.Path, ct))
            .Where(info => info is not null)
            .Cast<AppCacheInfo>()
            .ToList();

        // Объединение одинаковых имён из разных корней профиля.
        var merged = new Dictionary<string, (string Path, List<CleanupTarget> Targets, long Bytes)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var info in scanned)
        {
            ct.ThrowIfCancellationRequested();
            if (merged.TryGetValue(info.AppName, out var existing))
            {
                existing.Targets.AddRange(info.Targets);
                existing.Bytes += info.TotalBytes;
                merged[info.AppName] = existing;
            }
            else
            {
                merged[info.AppName] = (info.FolderPath, [.. info.Targets], info.TotalBytes);
            }
        }

        var result = merged
            .Select(pair => new AppCacheInfo(pair.Key, pair.Value.Path, pair.Value.Targets, pair.Value.Bytes))
            .Where(info => info.TotalBytes > 0)
            .OrderByDescending(info => info.TotalBytes)
            .ToList();

        _logger.Info($"APPCACHE | scan | apps={result.Count} | bytes={result.Sum(info => info.TotalBytes)} | {stopwatch.ElapsedMilliseconds} ms");
        return result;
    }

    private AppCacheInfo? MeasureApp(string appName, string appDirectory, CancellationToken ct)
    {
        var targets = new List<CleanupTarget>();
        foreach (var cacheDirectory in FindCacheDirectories(appDirectory, ct))
        {
            var relative = Path.GetRelativePath(appDirectory, cacheDirectory);
            targets.Add(new CleanupTarget($"{appName}: {relative.Replace('\\', '/')}", cacheDirectory));
        }

        if (targets.Count == 0)
        {
            return null;
        }

        var bytes = FileCleanupService.MeasureBytes(targets);
        return bytes > 0 ? new AppCacheInfo(appName, appDirectory, targets, bytes) : null;
    }

    // Обход папки приложения в ширину до MaxCacheDepth; найденный кэш-каталог
    // в цель добавляется, но внутрь не рекурсируется (его содержимое — сами
    // временные файлы, а не «приложение внутри приложения»).
    private IEnumerable<string> FindCacheDirectories(string appDirectory, CancellationToken ct)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((appDirectory, 0));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(current, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    // Reparse point — как в FileCleanupService: junction не даёт
                    // рекурсии уйти за пределы папки приложения.
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                var leaf = Path.GetFileName(subdirectory);
                if (IsCacheDirectoryName(leaf))
                {
                    yield return subdirectory;
                }
                else if (depth + 1 < MaxCacheDepth)
                {
                    queue.Enqueue((subdirectory, depth + 1));
                }
            }
        }
    }

    private bool IsCacheDirectoryName(string name) =>
        _cacheDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsExcludedAppName(string name) =>
        ExcludedAppFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}
