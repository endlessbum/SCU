using SCU.Common;
using SCU.Models;
using Xunit;

namespace SCU.Tests;

// Правила рекомендаций MVP: каждое правило — факт из снимка против порога.
// Тексты не проверяются (локализация), проверяются стабильные Id и серьёзность.
public class RecommendationEngineTests
{
    private static SystemSnapshot Snapshot(Action<SystemSnapshot>? setup = null)
    {
        var snapshot = new SystemSnapshot { Timestamp = new DateTime(2026, 1, 1, 12, 0, 0) };
        setup?.Invoke(snapshot);
        return snapshot;
    }

    [Fact]
    public void Evaluate_LowDiskFreeOnSystemDrive_ReturnsWarning()
    {
        var snapshot = Snapshot(s => s.Disks = [new DiskSnapshot("C:", 476.9, 5.0, 99.0)]);

        var recommendations = RecommendationEngine.Evaluate(snapshot);

        var recommendation = Assert.Single(recommendations);
        Assert.Equal("disk.low_free", recommendation.Id);
        Assert.Equal(RecommendationSeverity.Warning, recommendation.Severity);
        Assert.Equal(3, recommendation.NavigationSectionNumber);
    }

    [Fact]
    public void Evaluate_LowFreePercentOnly_ReturnsWarning()
    {
        // Свободных ГБ достаточно (50 >= 10), но занято 99.5% → свободно 0.5% < 10%.
        var snapshot = Snapshot(s => s.Disks = [new DiskSnapshot("C:", 476.9, 50.0, 99.5)]);

        var recommendations = RecommendationEngine.Evaluate(snapshot);

        Assert.Contains(recommendations, r => r.Id == "disk.low_free");
    }

    [Fact]
    public void Evaluate_EnoughDiskFree_ReturnsNoDiskRecommendation()
    {
        var snapshot = Snapshot(s => s.Disks = [new DiskSnapshot("C:", 476.9, 200.0, 40.0)]);

        var recommendations = RecommendationEngine.Evaluate(snapshot);

        Assert.DoesNotContain(recommendations, r => r.Id == "disk.low_free");
    }

    [Theory]
    [InlineData(11, true)]
    [InlineData(10, false)]
    public void Evaluate_StartupCount_AroundThreshold(int startupCount, bool expected)
    {
        var snapshot = Snapshot(s => s.StartupCount = startupCount);

        var recommendations = RecommendationEngine.Evaluate(snapshot);

        Assert.Equal(expected, recommendations.Any(r => r.Id == "startup.many"));
        var recommendation = recommendations.FirstOrDefault(r => r.Id == "startup.many");
        if (recommendation is not null)
        {
            Assert.Equal(RecommendationSeverity.Info, recommendation.Severity);
            Assert.Equal(7, recommendation.NavigationSectionNumber);
        }
    }

    [Fact]
    public void Evaluate_UpdateBlocked_ReturnsInfo()
    {
        var snapshot = Snapshot(s => s.UpdateBlocked = true);

        var recommendations = RecommendationEngine.Evaluate(snapshot);

        var recommendation = Assert.Single(recommendations);
        Assert.Equal("updates.blocked", recommendation.Id);
        Assert.Equal(RecommendationSeverity.Info, recommendation.Severity);
        Assert.Equal(16, recommendation.NavigationSectionNumber);
    }

    [Fact]
    public void Evaluate_UpdatePaused_ReturnsInfo()
    {
        var snapshot = Snapshot(s => s.UpdatePaused = true);

        var recommendations = RecommendationEngine.Evaluate(snapshot);

        var recommendation = Assert.Single(recommendations);
        Assert.Equal("updates.paused", recommendation.Id);
        Assert.Equal(RecommendationSeverity.Info, recommendation.Severity);
    }

    [Fact]
    public void Evaluate_AreaErrors_ReturnsWarningWithoutNavigation()
    {
        var snapshot = Snapshot(s => s.AreaErrors["tasks"] = "SCU.ps1 не найден.");

        var recommendations = RecommendationEngine.Evaluate(snapshot);

        var recommendation = Assert.Single(recommendations);
        Assert.Equal("scan.area_errors", recommendation.Id);
        Assert.Equal(RecommendationSeverity.Warning, recommendation.Severity);
        Assert.Null(recommendation.NavigationSectionNumber);
    }

    [Fact]
    public void Evaluate_EmptySnapshot_NoFakeRecommendations()
    {
        // Все поля null — ни одной выдуманной рекомендации.
        var recommendations = RecommendationEngine.Evaluate(Snapshot(null));

        Assert.Empty(recommendations);
    }

    [Fact]
    public void Evaluate_PreferenceDifferences_AreNotProblems()
    {
        // Активная схема питания и сетевые значения — предпочтения, не проблемы.
        var snapshot = Snapshot(s =>
        {
            s.ActivePlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
            s.Network = new NetworkSummarySnapshot("normal", "disabled", "0%", "0");
            s.UacState = "weakened";
        });

        var recommendations = RecommendationEngine.Evaluate(snapshot);

        Assert.Empty(recommendations);
    }
}
