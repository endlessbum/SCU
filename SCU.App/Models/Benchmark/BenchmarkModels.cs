using SCU.Services.Dashboard;

namespace SCU.Models.Benchmark;

// Состояние одной метрики бэнчмарка. Unknown (не удалось прочитать) и
// NotApplicable (неприменимо к системе) исключаются из расчёта индекса —
// они не равны нулю и показываются отдельно (покрытие).
public enum BenchmarkMetricState
{
    Ok,
    Unknown,
    NotApplicable,
    RequiresRestart
}

// Результат одной метрики: соответствие целевому состоянию 0..1 при Ok.
// NumericValue/TextValue — сырое значение для отображения (единицы и подписи — на слое UI).
public sealed record BenchmarkMetricResult(
    string Id,
    string Category,
    int Weight,
    BenchmarkMetricState State,
    double Conformity,
    double? NumericValue,
    string? TextValue,
    string? Note);

// Индекс одной категории: та же формула, что у общего индекса, внутри категории.
public sealed record BenchmarkCategoryResult(
    string Category,
    int WeightKnown,
    int Index,
    int MetricsKnown,
    int MetricsTotal);

// Итог одного расчёта: индекс 0..100, покрытие, категории, метрики и потенциал.
public sealed record BenchmarkResult(
    int AlgorithmVersion,
    DateTime Timestamp,
    int Index,
    int Coverage,
    IReadOnlyList<BenchmarkCategoryResult> Categories,
    IReadOnlyList<BenchmarkMetricResult> Metrics,
    int Potential,
    IReadOnlyList<string> UnreadAreas);

// Результат одного запуска бэнчмарка. Хранится в state\benchmark.json
// (журнал запусков, ограниченной ёмкости).
public sealed record BenchmarkRun(
    int AlgorithmVersion,
    DateTime Timestamp,
    int Index,
    int Coverage,
    int Potential,
    IReadOnlyList<BenchmarkMetricResult> Metrics)
{
    public const string ProfileUniversal = "universal";
}
