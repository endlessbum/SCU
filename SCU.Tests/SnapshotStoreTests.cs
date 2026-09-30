using SCU.Common;
using SCU.Models;
using Xunit;

namespace SCU.Tests;

// SnapshotStore: сохранение/чтение снимков, вытеснение старых записей (cap 50)
// и восстановление после повреждения файла. Файл — во временной папке теста.
public sealed class SnapshotStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;
    private readonly Logger _logger;
    private readonly SnapshotStore _store;

    public SnapshotStoreTests()
    {
        // Уникальная временная папка на каждый тест; удаление — в Dispose.
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "snapshots.json");
        _logger = Logger.CreateForCurrentRun();
        _store = new SnapshotStore(_logger, _filePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична: сбой удаления не роняет тест.
        }
    }

    private static SystemSnapshot FilledSnapshot(DateTime timestamp, string marker) => new()
    {
        Timestamp = timestamp,
        AppVersion = "1.0.0.0",
        WindowsVersion = "Windows 11 " + marker,
        WindowsBuild = "26100",
        Cpu = "Test CPU " + marker,
        Ram = "16 ГБ",
        Gpu = "Test GPU",
        Disks = [new DiskSnapshot("C:", 476.9, 200.0, 58.0)],
        StartupCount = 11,
        ServicesOk = 40,
        ServicesChanged = 3,
        ServicesTotal = 43,
        TasksTotal = 120,
        TasksDisabled = 5,
        Network = new NetworkSummarySnapshot("normal", "disabled", "0%", "0"),
        ActivePlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
        UpdateBlocked = false,
        UpdatePaused = true,
        UacState = "standard",
        PrivacyAppliedCount = 2,
        PrivacyTotal = 10
    };

    [Fact]
    public async Task SaveAsync_LoadAllAsync_RoundtripPreservesFields()
    {
        var snapshot = FilledSnapshot(new DateTime(2026, 1, 1, 12, 0, 0), "rt");
        snapshot.AreaErrors["tasks"] = "SCU.ps1 не найден.";

        await _store.SaveAsync(snapshot);
        var loaded = await _store.LoadAllAsync();

        var restored = Assert.Single(loaded);
        Assert.Equal(snapshot.Timestamp, restored.Timestamp);
        Assert.Equal(snapshot.AppVersion, restored.AppVersion);
        Assert.Equal(snapshot.WindowsVersion, restored.WindowsVersion);
        Assert.Equal(snapshot.WindowsBuild, restored.WindowsBuild);
        Assert.Equal(snapshot.Cpu, restored.Cpu);
        Assert.Equal(snapshot.Ram, restored.Ram);
        Assert.Equal(snapshot.Gpu, restored.Gpu);
        Assert.Equal(snapshot.Disks, restored.Disks);
        Assert.Equal(snapshot.StartupCount, restored.StartupCount);
        Assert.Equal(snapshot.ServicesOk, restored.ServicesOk);
        Assert.Equal(snapshot.ServicesChanged, restored.ServicesChanged);
        Assert.Equal(snapshot.ServicesTotal, restored.ServicesTotal);
        Assert.Equal(snapshot.TasksTotal, restored.TasksTotal);
        Assert.Equal(snapshot.TasksDisabled, restored.TasksDisabled);
        Assert.Equal(snapshot.Network, restored.Network);
        Assert.Equal(snapshot.ActivePlanGuid, restored.ActivePlanGuid);
        Assert.Equal(snapshot.UpdateBlocked, restored.UpdateBlocked);
        Assert.Equal(snapshot.UpdatePaused, restored.UpdatePaused);
        Assert.Equal(snapshot.UacState, restored.UacState);
        Assert.Equal(snapshot.PrivacyAppliedCount, restored.PrivacyAppliedCount);
        Assert.Equal(snapshot.PrivacyTotal, restored.PrivacyTotal);
        Assert.Equal(snapshot.AreaErrors, restored.AreaErrors);
    }

    [Fact]
    public async Task SaveAsync_OverCapacity_KeepsOnlyNewest50()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0);
        var saved = Enumerable.Range(0, 55)
            .Select(index => new SystemSnapshot
            {
                Timestamp = baseTime.AddMinutes(index),
                Cpu = "cpu-" + index
            })
            .ToList();

        foreach (var snapshot in saved)
        {
            await _store.SaveAsync(snapshot);
        }

        var loaded = await _store.LoadAllAsync();

        Assert.Equal(50, loaded.Count);
        // Хронологическая запись: первые 5 (самые старые) вытеснены.
        Assert.DoesNotContain(loaded, s => s.Timestamp == saved[0].Timestamp);
        Assert.Equal(saved[5].Timestamp, loaded[0].Timestamp);
        Assert.Equal(saved[^1].Timestamp, loaded[^1].Timestamp);
    }

    [Fact]
    public async Task LoadAllAsync_CorruptedFile_ReturnsEmptyAndMovesFileAside()
    {
        await File.WriteAllTextAsync(_filePath, "{ это не json ]");

        var loaded = await _store.LoadAllAsync();

        Assert.Empty(loaded);
        Assert.False(File.Exists(_filePath));
        // Повреждённый файл сохранён рядом под именем *.corrupt-<timestamp>.
        Assert.NotEmpty(Directory.GetFiles(_directory, "snapshots.json.corrupt-*"));
    }
}
