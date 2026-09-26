using SCU.Common;
using SCU.Models;
using SCU.Services.Dashboard;
using Xunit;

namespace SCU.Tests;

// BaselineStore: roundtrip, ёмкость 1 (второй baseline вытесняет первый),
// очистка и восстановление после повреждения файла. Файл — во временной папке теста.
public sealed class BaselineStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;
    private readonly Logger _logger;
    private readonly BaselineStore _store;

    public BaselineStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "baseline.json");
        _logger = Logger.CreateForCurrentRun();
        _store = new BaselineStore(_logger, _filePath);
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

    private static SystemSnapshot Snapshot(DateTime timestamp, string marker) => new()
    {
        Timestamp = timestamp,
        WindowsVersion = "Windows 11 " + marker,
        Cpu = "Test CPU " + marker,
        Disks = [new DiskSnapshot("C:", 476.9, 200.0, 58.0)],
        StartupCount = 11,
        ServicesOk = 40,
        Network = new NetworkSummarySnapshot("normal", "disabled", "0%", "0"),
        UacState = "standard"
    };

    [Fact]
    public async Task SaveAsync_LoadAsync_RoundtripPreservesFields()
    {
        var snapshot = Snapshot(new DateTime(2026, 9, 1, 12, 0, 0), "baseline");

        await _store.SaveAsync(snapshot);
        var restored = await _store.LoadAsync();

        Assert.NotNull(restored);
        Assert.Equal(snapshot.Timestamp, restored!.Timestamp);
        Assert.Equal(snapshot.WindowsVersion, restored.WindowsVersion);
        Assert.Equal(snapshot.Cpu, restored.Cpu);
        Assert.Equal(snapshot.Disks, restored.Disks);
        Assert.Equal(snapshot.StartupCount, restored.StartupCount);
        Assert.Equal(snapshot.ServicesOk, restored.ServicesOk);
        Assert.Equal(snapshot.Network, restored.Network);
        Assert.Equal(snapshot.UacState, restored.UacState);
    }

    [Fact]
    public async Task LoadAsync_NoFile_ReturnsNull()
    {
        Assert.Null(await _store.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_SecondBaseline_ReplacesFirstCapacity1()
    {
        await _store.SaveAsync(Snapshot(new DateTime(2026, 9, 1, 10, 0, 0), "first"));
        await _store.SaveAsync(Snapshot(new DateTime(2026, 9, 2, 10, 0, 0), "second"));

        var restored = await _store.LoadAsync();

        Assert.NotNull(restored);
        // Ёмкость 1: остаётся только последний сохранённый baseline.
        Assert.Equal(new DateTime(2026, 9, 2, 10, 0, 0), restored!.Timestamp);
        Assert.Equal("Test CPU second", restored.Cpu);
    }

    [Fact]
    public async Task ClearAsync_RemovesBaseline()
    {
        await _store.SaveAsync(Snapshot(new DateTime(2026, 9, 1, 10, 0, 0), "clear"));

        await _store.ClearAsync();

        Assert.Null(await _store.LoadAsync());
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public async Task LoadAsync_CorruptedFile_ReturnsNullAndMovesFileAside()
    {
        await File.WriteAllTextAsync(_filePath, "{ это не json ]");

        var restored = await _store.LoadAsync();

        Assert.Null(restored);
        Assert.False(File.Exists(_filePath));
        // Повреждённый файл сохранён рядом: данные не потеряны молча.
        Assert.NotEmpty(Directory.GetFiles(_directory, "baseline.json.corrupt-*"));
    }
}
