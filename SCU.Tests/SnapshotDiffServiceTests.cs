using SCU.Models;
using SCU.Services.Dashboard;
using Xunit;

namespace SCU.Tests;

// SnapshotDiffService: контролируемый diff двух снимков. Проверяются правила null
// (оба неизвестны — записи нет; известен один — запись с null), дельты по числам
// и сопоставление дисков по букве.
public sealed class SnapshotDiffServiceTests
{
    private static SystemSnapshot Snapshot(Action<SystemSnapshot>? setup = null)
    {
        var snapshot = new SystemSnapshot { Timestamp = new DateTime(2026, 9, 1, 10, 0, 0) };
        setup?.Invoke(snapshot);
        return snapshot;
    }

    [Fact]
    public void Diff_IdenticalSnapshots_ReturnsEmpty()
    {
        SystemSnapshot Filled() => Snapshot(s =>
        {
            s.Cpu = "Test CPU";
            s.StartupCount = 5;
            s.Network = new NetworkSummarySnapshot("normal", "disabled", "0%", "0");
            s.Disks = [new DiskSnapshot("C:", 476.9, 200.0, 58.0)];
        });

        var diff = SnapshotDiffService.Diff(Filled(), Filled());

        Assert.Empty(diff);
    }

    [Fact]
    public void Diff_BothFieldsNull_NoEntry()
    {
        // Gpu не заполнен ни в одном снимке — записи быть не должно.
        var diff = SnapshotDiffService.Diff(Snapshot(), Snapshot());

        Assert.DoesNotContain(diff, entry => entry.Field == "Gpu");
    }

    [Fact]
    public void Diff_FieldKnownOnlyInOneSnapshot_EntryWithNullOnOtherSide()
    {
        // Поле было null («было неизвестно»), стало значением — честная запись.
        var before = Snapshot();
        var after = Snapshot(s => s.Gpu = "Test GPU");
        var diff = SnapshotDiffService.Diff(before, after);

        var entry = Assert.Single(diff);
        Assert.Equal("Gpu", entry.Field);
        Assert.Null(entry.Before);
        Assert.Equal("Test GPU", entry.After);
        Assert.Null(entry.Delta);
    }

    [Fact]
    public void Diff_FieldKnownOnlyInBeforeSnapshot_EntryWithNullAfter()
    {
        var before = Snapshot(s => s.UacState = "standard");
        var after = Snapshot();

        var entry = Assert.Single(SnapshotDiffService.Diff(before, after));

        Assert.Equal("UacState", entry.Field);
        Assert.Equal("standard", entry.Before);
        Assert.Null(entry.After);
    }

    [Fact]
    public void Diff_CountChanges_HaveNumericDelta()
    {
        var before = Snapshot(s =>
        {
            s.StartupCount = 10;
            s.ServicesOk = 40;
            s.ServicesTotal = 43;
        });
        var after = Snapshot(s =>
        {
            s.StartupCount = 12;
            s.ServicesOk = 39;
            s.ServicesTotal = 43; // не изменилось — записи не будет
        });

        var diff = SnapshotDiffService.Diff(before, after);

        Assert.Equal(2, diff.Count);
        var startup = diff.Single(entry => entry.Field == "StartupCount");
        Assert.Equal((object)10, startup.Before);
        Assert.Equal((object)12, startup.After);
        Assert.Equal(2, startup.Delta);
        var services = diff.Single(entry => entry.Field == "ServicesOk");
        Assert.Equal(-1, services.Delta);
    }

    [Fact]
    public void Diff_BooleansAndStrings_NoDelta()
    {
        var before = Snapshot(s =>
        {
            s.UpdateBlocked = false;
            s.UpdatePaused = false;
            s.WindowsBuild = "26100";
        });
        var after = Snapshot(s =>
        {
            s.UpdateBlocked = true;
            s.UpdatePaused = null;
            s.WindowsBuild = "26200";
        });

        var diff = SnapshotDiffService.Diff(before, after);

        Assert.All(diff, entry => Assert.Null(entry.Delta));
        var blocked = diff.Single(entry => entry.Field == "UpdateBlocked");
        Assert.Equal((object)false, blocked.Before);
        Assert.Equal((object)true, blocked.After);
        Assert.Contains(diff, entry => entry.Field == "UpdatePaused");
        Assert.Contains(diff, entry => entry.Field == "WindowsBuild");
    }

    [Fact]
    public void Diff_NetworkComponents_ComparedIndividually()
    {
        var before = Snapshot(s => s.Network = new NetworkSummarySnapshot("normal", "disabled", "0%", null));
        var after = Snapshot(s => s.Network = new NetworkSummarySnapshot("disabled", "disabled", "0%", "0"));

        var diff = SnapshotDiffService.Diff(before, after);

        Assert.Equal(2, diff.Count);
        Assert.Contains(diff, entry => entry.Field == "Network.AutoTuning");
        Assert.Contains(diff, entry => entry.Field == "Network.NetBios");
    }

    [Fact]
    public void Diff_DisksByLetter_FreeGbDelta()
    {
        var before = Snapshot(s => s.Disks =
        [
            new DiskSnapshot("C:", 476.9, 200.0, 58.0),
            new DiskSnapshot("D:", 931.5, 100.0, 89.0)
        ]);
        var after = Snapshot(s => s.Disks =
        [
            // Буква приходит из WMI с ':' — сопоставление должно нормализоваться.
            new DiskSnapshot("C:", 476.9, 180.5, 62.0),
            new DiskSnapshot("D:", 931.5, 100.0, 89.0),
            new DiskSnapshot("E:", 15.0, 3.0, 80.0)
        ]);

        var diff = SnapshotDiffService.Diff(before, after);

        // Две записи: изменившийся C и новый диск E (D не изменился — записи нет).
        Assert.Equal(2, diff.Count);
        var entry = diff.Single(item => item.Field == SnapshotDiffService.DiskFreeField("C"));
        Assert.Equal((object)200.0, entry.Before);
        Assert.Equal((object)180.5, entry.After);
        Assert.Equal(-19.5, entry.Delta ?? 0, precision: 9);
        var newDisk = diff.Single(item => item.Field == SnapshotDiffService.DiskFreeField("E"));
        Assert.Null(newDisk.Before);
    }

    [Fact]
    public void Diff_NewDiskOnlyInAfter_BeforeIsUnknown()
    {
        var before = Snapshot();
        var after = Snapshot(s => s.Disks = [new DiskSnapshot("Q:", 15.0, 3.0, 80.0)]);

        var entry = Assert.Single(SnapshotDiffService.Diff(before, after));

        Assert.Equal(SnapshotDiffService.DiskFreeField("Q"), entry.Field);
        Assert.Null(entry.Before);
        Assert.Equal((object)3.0, entry.After);
        Assert.Null(entry.Delta);
    }

    [Fact]
    public void Diff_NullSnapshots_ReturnsEmpty()
    {
        Assert.Empty(SnapshotDiffService.Diff(null, Snapshot()));
        Assert.Empty(SnapshotDiffService.Diff(Snapshot(), null));
    }
}
