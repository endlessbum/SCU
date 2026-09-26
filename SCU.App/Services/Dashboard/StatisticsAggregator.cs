using SCU.Models;

namespace SCU.Services.Dashboard;

// Счётчик операций одной категории.
public sealed record CategoryStatistic(string Category, int Operations);

// Итог агрегации истории за период. Categories — операции по категориям БЕЗ событий
// сканирования (их счёт отдаётся отдельно в ScanCount); BytesFreed — сумма реально
// измеренных байтов очистки (BytesFreed, null не учитывается); Failures — события со
// статусом "fail" за период (включая неудачные проверки).
public sealed record StatisticsSummary(
    IReadOnlyList<CategoryStatistic> Categories,
    long BytesFreed,
    int Failures,
    int ScanCount);

// Чистый агрегатор истории операций: вход — список HistoryEvent и период в днях
// (null = всё время). Никаких выдуманных метрик: только подсчёт событий из истории.
public static class StatisticsAggregator
{
    // Категория сканирования записывается в языке активного интерфейса (L.T при записи
    // события), поэтому учитываем оба варианта: русский ключ и английский перевод.
    private const string ScanCategoryRu = "Состояние ПК";
    private const string ScanCategoryEn = "PC Status";

    public static StatisticsSummary Summarize(IReadOnlyList<HistoryEvent> events, int? periodDays)
    {
        // null = «всё время»: границы периода нет, фильтр по времени не применяется.
        DateTime? cutoff = periodDays is { } days
            ? DateTime.Now - TimeSpan.FromDays(days)
            : null;
        var inPeriod = events
            .Where(@event => cutoff is null || @event.Timestamp >= cutoff.Value)
            .ToList();

        var categories = inPeriod
            .Where(@event => !IsScanCategory(@event.Category))
            .GroupBy(@event => @event.Category, StringComparer.Ordinal)
            .Select(group => new CategoryStatistic(group.Key, group.Count()))
            .OrderByDescending(statistic => statistic.Operations)
            .ThenBy(statistic => statistic.Category, StringComparer.Ordinal)
            .ToList();

        return new StatisticsSummary(
            categories,
            inPeriod.Sum(@event => @event.BytesFreed ?? 0),
            inPeriod.Count(@event => !string.Equals(@event.Status, HistoryEvent.StatusOk, StringComparison.Ordinal)),
            inPeriod.Count(@event => IsScanCategory(@event.Category)));
    }

    private static bool IsScanCategory(string category) =>
        string.Equals(category, ScanCategoryRu, StringComparison.Ordinal)
        || string.Equals(category, ScanCategoryEn, StringComparison.Ordinal);
}
