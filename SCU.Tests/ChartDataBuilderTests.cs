using SCU.Models;
using SCU.Services.Dashboard;
using Xunit;

namespace SCU.Tests;

// ChartDataBuilder: серии графиков «Трендов» — только реально измеренные точки,
// пропуск снимков без нужного диска, равномерное прореживание без интерполяции
// и фильтр очисток по измеренным байтам.
public sealed class ChartDataBuilderTests
{
    private static readonly DateTime Base = new(2026, 3, 5, 9, 0, 0);

    private static SystemSnapshot Snapshot(int minutes, params (string Letter, double Free)[] disks) => new()
    {
        Timestamp = Base.AddMinutes(minutes),
        Disks = disks.Select(disk => new DiskSnapshot(disk.Letter, 500, disk.Free, null)).ToList()
    };

    private static HistoryEvent Cleanup(int minutes, long? bytesFreed, string category = "Очистка") =>
        new(Base.AddMinutes(minutes), category, "Операция", HistoryEvent.StatusOk, null, bytesFreed);

    [Fact]
    public void FreeDiskSpaceSeries_UsesOnlyRequestedDrive()
    {
        var snapshots = new List<SystemSnapshot>
        {
            Snapshot(0, ("C:", 100), ("D:", 900)),
            Snapshot(10, ("C:", 90), ("D:", 950))
        };

        var series = ChartDataBuilder.FreeDiskSpaceSeries(snapshots, "C:", maxPoints: 30);

        Assert.Equal(2, series.Count);
        Assert.Equal([100d, 90d], series.Select(point => point.Value));
        Assert.Equal(Base, series[0].Timestamp);
        Assert.Equal(Base.AddMinutes(10), series[1].Timestamp);
    }

    [Fact]
    public void FreeDiskSpaceSeries_LetterNormalization_IgnoresColonAndBackslash()
    {
        var snapshots = new List<SystemSnapshot> { Snapshot(0, ("C:", 100)), Snapshot(5, ("C:", 80)) };

        // Вызывающий передаёт букву без двоеточия — серия та же.
        var series = ChartDataBuilder.FreeDiskSpaceSeries(snapshots, "c", maxPoints: 30);

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public void FreeDiskSpaceSeries_SkipsSnapshotsWithoutDrive()
    {
        var snapshots = new List<SystemSnapshot>
        {
            Snapshot(0, ("C:", 100)),
            // Снимок без диска C: — в серию не попадает и не создаёт «дырку».
            Snapshot(10, ("D:", 900)),
            Snapshot(20, ("C:", 70))
        };

        var series = ChartDataBuilder.FreeDiskSpaceSeries(snapshots, "C:", maxPoints: 30);

        Assert.Equal(2, series.Count);
        Assert.Equal([100d, 70d], series.Select(point => point.Value));
        Assert.Equal(Base.AddMinutes(20), series[1].Timestamp);
    }

    [Fact]
    public void FreeDiskSpaceSeries_LessThanTwoPoints_ReturnsEmpty()
    {
        Assert.Empty(ChartDataBuilder.FreeDiskSpaceSeries([], "C:", 30));
        Assert.Empty(ChartDataBuilder.FreeDiskSpaceSeries([Snapshot(0, ("C:", 100))], "C:", 30));
        // Единственный снимок с чужим диском — тем более пусто.
        Assert.Empty(ChartDataBuilder.FreeDiskSpaceSeries([Snapshot(0, ("D:", 100))], "C:", 30));
    }

    [Fact]
    public void FreeDiskSpaceSeries_ThinsEvenly_KeepingRealValues()
    {
        var snapshots = Enumerable.Range(0, 10)
            .Select(index => Snapshot(index * 10, ("C:", 100 - index)))
            .ToList();

        var series = ChartDataBuilder.FreeDiskSpaceSeries(snapshots, "C:", maxPoints: 3);

        Assert.Equal(3, series.Count);
        // Первая и последняя точки сохранены, средняя — реальная точка источника.
        Assert.Equal(100, series[0].Value);
        Assert.Equal(91, series[^1].Value);
        Assert.Contains(series[1].Value, new[] { 100d, 99, 98, 97, 96, 95, 94, 93, 92, 91 });
        // Монотонность по времени не нарушена (прореживание не переставляет точки).
        Assert.Equal(series.OrderBy(point => point.Timestamp), series);
    }

    [Fact]
    public void FreeDiskSpaceSeries_FewerPointsThanLimit_KeepsAll()
    {
        var snapshots = new List<SystemSnapshot> { Snapshot(0, ("C:", 100)), Snapshot(5, ("C:", 95)) };

        var series = ChartDataBuilder.FreeDiskSpaceSeries(snapshots, "C:", maxPoints: 30);

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public void FreeDiskSpaceSeries_SortsSnapshotsByTimestamp()
    {
        // Снимки переданы в произвольном порядке: серия всё равно хронологическая.
        var snapshots = new List<SystemSnapshot> { Snapshot(20, ("C:", 70)), Snapshot(0, ("C:", 100)) };

        var series = ChartDataBuilder.FreeDiskSpaceSeries(snapshots, "C:", maxPoints: 30);

        Assert.Equal([100d, 70d], series.Select(point => point.Value));
    }

    [Fact]
    public void CleanupBytesSeries_KeepsOnlyMeasuredCleanupBytes()
    {
        var events = new List<HistoryEvent>
        {
            Cleanup(0, 1024),
            // Без измеренных байтов — в серию не входит.
            Cleanup(5, null),
            // Другая категория с байтами — тоже не «очистка».
            Cleanup(10, 4096, "Службы"),
            Cleanup(15, 2048)
        };

        var series = ChartDataBuilder.CleanupBytesSeries(events, maxBars: 12);

        Assert.Equal(2, series.Count);
        Assert.Equal([1024d, 2048d], series.Select(point => point.Value));
        Assert.Equal(Base.AddMinutes(15), series[1].Timestamp);
    }

    [Fact]
    public void CleanupBytesSeries_EnglishCategory_IsRecognized()
    {
        // Категория пишется в языке активного интерфейса при записи события.
        var events = new List<HistoryEvent> { Cleanup(0, 512, "Cleanup") };

        var series = ChartDataBuilder.CleanupBytesSeries(events, maxBars: 12);

        Assert.Equal(512, Assert.Single(series).Value);
    }

    [Fact]
    public void CleanupBytesSeries_KeepsLastBarsByTime()
    {
        var events = Enumerable.Range(0, 5).Select(index => Cleanup(index * 10, (index + 1) * 100)).ToList();

        var series = ChartDataBuilder.CleanupBytesSeries(events, maxBars: 3);

        // Последние три по времени, в возрастающем порядке.
        Assert.Equal([300d, 400d, 500d], series.Select(point => point.Value));
    }

    [Fact]
    public void CleanupBytesSeries_NoCommands_ReturnsEmpty()
    {
        Assert.Empty(ChartDataBuilder.CleanupBytesSeries([], 12));
        Assert.Empty(ChartDataBuilder.CleanupBytesSeries(
            [Cleanup(0, 1024, "Автозагрузка"), Cleanup(5, null)],
            12));
    }
}
