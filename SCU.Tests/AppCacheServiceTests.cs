using SCU.Common;
using SCU.Infrastructure.Windows.Maintenance;
using Xunit;

namespace SCU.Tests;

// Раздел 26 «Кэш приложений»: обнаружение кэш-каталогов в профиле, замер,
// защита системных папок. Реальный профиль не затрагивается — корни передаются
// в сервис параметрами (temp-каталог).
public sealed class AppCacheServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _root2;
    private readonly Logger _logger;
    private readonly AppCacheService _service;

    public AppCacheServiceTests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(directory, "local");
        _root2 = Path.Combine(directory, "roaming");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_root2);
        _logger = Logger.CreateForCurrentRun();
        _service = new AppCacheService(_logger, [_root, _root2]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch
        {
            // Временная папка не критична.
        }
    }

    private string WriteFile(params string[] pathParts)
    {
        var path = Path.Combine([_root, .. pathParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', 100));
        return path;
    }

    [Fact]
    public async Task Scan_FindsCacheDirectory_AndSumsFileSizes()
    {
        WriteFile("SomeApp", "Cache", "a.bin");
        WriteFile("SomeApp", "Cache", "b.bin");

        var result = await _service.ScanAsync();

        var entry = Assert.Single(result);
        Assert.Equal("SomeApp", entry.AppName);
        Assert.Equal(200, entry.TotalBytes);
        Assert.Single(entry.Targets);
    }

    // Практический случай Chromium: кэш на глубине 4 от папки приложения
    // (Google\Chrome\User Data\<profile>\Code Cache) — приложение «Google».
    [Fact]
    public async Task Scan_NestedChromiumStyle_CacheAtDepthFour_IsFound()
    {
        WriteFile("Google", "Chrome", "User Data", "Default", "Code Cache", "data.bin");

        var result = await _service.ScanAsync();

        var entry = Assert.Single(result);
        Assert.Equal("Google", entry.AppName);
        Assert.Equal(100, entry.TotalBytes);
    }

    // Глубже 4 уровней кэш не ищется: глубже лежат данные, а не временные файлы.
    [Fact]
    public async Task Scan_CacheDeeperThanFourLevels_IsIgnored()
    {
        WriteFile("SomeApp", "L1", "L2", "L3", "Cache", "a.bin");
        WriteFile("OtherApp", "L1", "L2", "L3", "L4", "Cache", "b.bin");

        var result = await _service.ScanAsync();

        var entry = Assert.Single(result);
        Assert.Equal("SomeApp", entry.AppName);
    }

    [Fact]
    public async Task Scan_ExcludesProtectedFolderNames()
    {
        WriteFile("Microsoft", "Cache", "a.bin");
        WriteFile("Windows", "Temp", "b.bin");
        WriteFile("SCU", "Cache", "c.bin");
        WriteFile("SomeApp", "Cache", "d.bin");

        var result = await _service.ScanAsync();

        var entry = Assert.Single(result);
        Assert.Equal("SomeApp", entry.AppName);
    }

    [Fact]
    public async Task Scan_AppWithoutCacheDirectories_IsAbsent()
    {
        WriteFile("SomeApp", "config", "settings.json");

        var result = await _service.ScanAsync();

        Assert.Empty(result);
    }

    // Одно имя приложения в Local и Roaming — одна карточка с объединёнными
    // целями и суммарным объёмом.
    [Fact]
    public async Task Scan_SameAppNameAcrossRoots_MergesIntoSingleEntry()
    {
        var localCache = Path.Combine(_root, "SomeApp", "Cache");
        Directory.CreateDirectory(localCache);
        await File.WriteAllTextAsync(Path.Combine(localCache, "a.bin"), new string('x', 100));
        var roamingCache = Path.Combine(_root2, "SomeApp", "Code Cache");
        Directory.CreateDirectory(roamingCache);
        await File.WriteAllTextAsync(Path.Combine(roamingCache, "b.bin"), new string('x', 50));

        var result = await _service.ScanAsync();

        var entry = Assert.Single(result);
        Assert.Equal(150, entry.TotalBytes);
        Assert.Equal(2, entry.Targets.Count);
    }

    [Fact]
    public async Task Scan_SortsBySizeDescending()
    {
        var small = Path.Combine(_root, "SmallApp", "Cache");
        Directory.CreateDirectory(small);
        await File.WriteAllTextAsync(Path.Combine(small, "a.bin"), new string('x', 100));
        var big = Path.Combine(_root, "BigApp", "Cache");
        Directory.CreateDirectory(big);
        await File.WriteAllTextAsync(Path.Combine(big, "b.bin"), new string('x', 500));

        var result = await _service.ScanAsync();

        Assert.Equal("BigApp", result[0].AppName);
        Assert.Equal("SmallApp", result[1].AppName);
    }

    // Штатная очистка по целям записи обнуляет объём: строка приложения
    // уходит из списка.
    [Fact]
    public async Task CleanByTargets_ReducesMeasuredSizeToZero()
    {
        WriteFile("SomeApp", "Cache", "a.bin");
        var entry = Assert.Single(await _service.ScanAsync());

        var cleanup = new FileCleanupService(_logger);
        var result = await cleanup.CleanAsync(entry.Targets, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, FileCleanupService.MeasureBytes(entry.Targets));
    }
}
