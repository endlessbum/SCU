using SCU.Models;
using SCU.Models.Benchmark;
using SCU.Services;
using Xunit;

namespace SCU.Tests;

// Чистый калькулятор индекса: Unknown/NotApplicable исключаются из расчёта
// (не равны нулю), покрытие считается отдельно, потенциал = доступные баллы.
public class BenchmarkServiceTests
{
    private static SystemSnapshot Snapshot(Action<SystemSnapshot>? setup = null)
    {
        var snapshot = new SystemSnapshot
        {
            Timestamp = DateTime.Now,
            WindowsVersion = "Windows 11",
            Disks = [new DiskSnapshot("C:", 476.0, 250.0, 47.5)],
            StartupCount = 5,
            ServicesTotal = 20,
            ServicesOk = 18,
            ServicesChanged = 2,
            TasksTotal = 15,
            TasksDisabled = 0,
            ActivePlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
            UacState = SystemStateService.UacWeakened,
            PrivacyAppliedCount = 0,
            PrivacyTotal = 6,
            UpdateBlocked = false,
            UpdatePaused = false,
            Network = new NetworkSummarySnapshot("normal", "default", "none", "enable"),
            AreaErrors = []
        };
        setup?.Invoke(snapshot);
        return snapshot;
    }

    private static BenchmarkService Service => new();

    [Fact]
    public void Evaluate_PerfectSnapshot_Index100_Coverage100()
    {
        var result = Service.Evaluate(Snapshot());

        Assert.Equal(100, result.Index);
        Assert.Equal(100, result.Coverage);
        Assert.Equal(0, result.Potential);
        Assert.Equal(BenchmarkService.AlgorithmVersion, result.AlgorithmVersion);
    }

    [Fact]
    public void Evaluate_UacWeakened_SecurityFull()
    {
        // Отключенный/ослабленный UAC — плюс по новой политике индекса.
        var result = Service.Evaluate(Snapshot(s => s.UacState = SystemStateService.UacWeakened));

        var uac = Assert.Single(result.Metrics, m => m.Id == "security.uac_disabled");
        Assert.Equal(1.0, uac.Conformity);
    }

    [Fact]
    public void Evaluate_UacStandard_SecurityZeroAndPotentialReflectsGap()
    {
        var result = Service.Evaluate(Snapshot(s => s.UacState = SystemStateService.UacStandard));

        var uac = Assert.Single(result.Metrics, m => m.Id == "security.uac_disabled");
        Assert.Equal(0.0, uac.Conformity);
        Assert.True(result.Index < 100);
        var expectedGap = (int)Math.Round(uac.Weight * (1.0 - uac.Conformity) * 1.0);
        Assert.True(result.Potential >= expectedGap);
    }

    [Fact]
    public void Evaluate_UnknownMetric_ExcludedFromIndexButCountsInCoverage()
    {
        // Область не прочитана: метрика UAC уходит в Unknown.
        var result = Service.Evaluate(Snapshot(s => s.AreaErrors[SystemStateService.AreaSecurity] = "отказ в доступе"));

        var uac = Assert.Single(result.Metrics, m => m.Id == "security.uac_disabled");
        Assert.Equal(BenchmarkMetricState.Unknown, uac.State);

        // Индекс считается только по известным метрикам — идеальные остальные дают 100.
        Assert.Equal(100, result.Index);
        // Покрытие ниже 100: вес неизвестной метрики исключён из знаменателя индекса,
        // но учтён в покрытии.
        Assert.True(result.Coverage < 100);
        Assert.Contains(SystemStateService.AreaSecurity, result.UnreadAreas);
    }

    [Fact]
    public void Evaluate_EmptySnapshot_AllUnknown_IndexZero_LowCoverage()
    {
        var result = Service.Evaluate(new SystemSnapshot { Timestamp = DateTime.Now, AreaErrors = [] });

        Assert.Equal(0, result.Index);
        Assert.Equal(0, result.Coverage);
        Assert.All(result.Metrics, m => Assert.Equal(BenchmarkMetricState.Unknown, m.State));
    }

    [Fact]
    public void Evaluate_PartialPrivacyEnabled_ScalesIndex()
    {
        var half = Service.Evaluate(Snapshot(s =>
        {
            s.PrivacyAppliedCount = 3;
            s.PrivacyTotal = 6;
        }));

        var privacy = Assert.Single(half.Metrics, m => m.Id == "privacy.enabled");
        Assert.Equal(0.5, privacy.Conformity);
        Assert.True(half.Index < 100);
        Assert.True(half.Potential > 0);
    }

    [Fact]
    public void Evaluate_LowDiskFree_PartialConformity()
    {
        var result = Service.Evaluate(Snapshot(s =>
        {
            s.Disks = [new DiskSnapshot("C:", 476.0, 5.0, 99.0)];
        }));

        var disk = Assert.Single(result.Metrics, m => m.Id == "disk.free_system");
        Assert.Equal(0.5, disk.Conformity, precision: 2);
        Assert.True(result.Index < 100);
    }

    [Fact]
    public void Evaluate_AllPrivacyApplied_EnabledZero()
    {
        // Все категории применены (тумблеры OFF) — включённых нет, очков нет.
        var none = Service.Evaluate(Snapshot(s => s.PrivacyAppliedCount = 6));

        var privacy = Assert.Single(none.Metrics, m => m.Id == "privacy.enabled");
        Assert.Equal(0.0, privacy.Conformity);
        Assert.Equal(0, privacy.NumericValue);
    }

    [Fact]
    public void Evaluate_StartupOverThreshold_DecreasesGradually()
    {
        var many = Service.Evaluate(Snapshot(s => s.StartupCount = 15));
        var startup = Assert.Single(many.Metrics, m => m.Id == "startup.count");
        Assert.Equal(0.5, startup.Conformity, precision: 2);
    }

    [Fact]
    public void Evaluate_Categories_CoverAllMetrics()
    {
        var result = Service.Evaluate(Snapshot());

        Assert.Equal(result.Metrics.Count, result.Categories.Sum(c => c.MetricsTotal));
        Assert.Equal(result.Metrics.Where(m => m.State == BenchmarkMetricState.Ok).Sum(m => m.Weight),
            result.Categories.Sum(c => c.WeightKnown));
    }

    [Fact]
    public void Evaluate_StaleRun_MissingMetricsAllUnknown()
    {
        // Снимок без областей (сбор не проводился) не должен оцениваться как «всё плохо».
        var result = Service.Evaluate(new SystemSnapshot { AreaErrors = [] });

        Assert.True(result.Coverage == 0);
        Assert.Equal(0, result.Potential);
    }
}
