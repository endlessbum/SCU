using SCU.Models;
using SCU.Services.Dashboard;
using Xunit;

namespace SCU.Tests;

// ProfileCompliance: чистая логика соответствия — счётчики matched/total,
// честное «не удалось прочитать» (unknown не засчитывается) и порядок строк
// по каталогу, а не по словарю цели.
public sealed class ProfileComplianceTests
{
    private static readonly ProfileStep[] Catalog =
    [
        new("power.ultimate", "Питание", "Максимальная производительность", ProfileTargetKind.PowerPlan, "guid-1"),
        new("network.gaming", "Сеть", "Игровой профиль сети", ProfileTargetKind.NetworkGaming, null),
        new("games.gamebar", "Игры", "Game Bar", ProfileTargetKind.GameSwitch, "game-bar")
    ];

    [Fact]
    public void Evaluate_MixedStates_CountsMatchedCorrectly()
    {
        var target = new Dictionary<string, StepDesire>
        {
            ["power.ultimate"] = StepDesire.On,
            ["network.gaming"] = StepDesire.On,
            ["games.gamebar"] = StepDesire.Off
        };
        var actual = new Dictionary<string, bool?>
        {
            ["power.ultimate"] = true,   // цель On, факт On — совпадение
            ["network.gaming"] = false,  // цель On, факт Off — несовпадение
            ["games.gamebar"] = false    // цель Off, факт Off — совпадение
        };

        var result = ProfileCompliance.Evaluate(Catalog, target, actual);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Matched);
        Assert.Equal(3, result.Entries.Count);
        Assert.True(result.Entries[0].Matches);
        Assert.False(result.Entries[1].Matches);
        Assert.True(result.Entries[2].Matches);
    }

    [Fact]
    public void Evaluate_UnknownState_NotCountedAsMatched()
    {
        var target = new Dictionary<string, StepDesire>
        {
            ["power.ultimate"] = StepDesire.On,
            ["network.gaming"] = StepDesire.On
        };
        // power.ultimate прочитать не удалось (null); network.gaming отсутствует в словаре фактов.
        var actual = new Dictionary<string, bool?> { ["power.ultimate"] = null };

        var result = ProfileCompliance.Evaluate(Catalog, target, actual);

        Assert.Equal(2, result.Total);
        Assert.Equal(0, result.Matched);
        Assert.All(result.Entries, entry => Assert.False(entry.Matches));
        // Факт отсутствует в словаре — тоже null, а не выдуманное значение.
        Assert.Null(result.Entries[1].Actual);
    }

    [Fact]
    public void Evaluate_EmptyTarget_ReturnsZeroOfZero()
    {
        var result = ProfileCompliance.Evaluate(
            Catalog,
            new Dictionary<string, StepDesire>(),
            new Dictionary<string, bool?> { ["power.ultimate"] = true });

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Matched);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Evaluate_TargetStepsOutsideCatalog_AreIgnored()
    {
        // Словарь цели мог остаться от старой версии приложения: шага больше нет
        // в каталоге — он не попадает в оценку и не ломает счётчики.
        var target = new Dictionary<string, StepDesire>
        {
            ["power.ultimate"] = StepDesire.On,
            ["removed.step"] = StepDesire.On
        };
        var actual = new Dictionary<string, bool?> { ["power.ultimate"] = true };

        var result = ProfileCompliance.Evaluate(Catalog, target, actual);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Matched);
        Assert.Equal("power.ultimate", Assert.Single(result.Entries).StepId);
    }

    [Fact]
    public void Evaluate_EntriesFollowCatalogOrder_NotTargetOrder()
    {
        var target = new Dictionary<string, StepDesire>
        {
            ["games.gamebar"] = StepDesire.Off,
            ["power.ultimate"] = StepDesire.On
        };
        var actual = new Dictionary<string, bool?> { ["power.ultimate"] = false, ["games.gamebar"] = true };

        var result = ProfileCompliance.Evaluate(Catalog, target, actual);

        Assert.Equal("power.ultimate", result.Entries[0].StepId);
        Assert.Equal("games.gamebar", result.Entries[1].StepId);
    }
}
