using SCU.Models;
using SCU.Services.Dashboard;
using Xunit;

namespace SCU.Tests;

// StatisticsAggregator: периоды (7/30/90/всё время), суммарные байты очистки,
// количество ошибок и исключение событий сканирования из счётчика операций.
// Временные метки строятся относительно DateTime.Now — так тест не зависит от даты запуска.
public sealed class StatisticsAggregatorTests
{
    private static HistoryEvent Event(
        DateTime timestamp,
        string category,
        string status = HistoryEvent.StatusOk,
        long? bytesFreed = null) =>
        new(timestamp, category, "Операция", status, null, bytesFreed);

    [Fact]
    public void Summarize_EmptyHistory_ReturnsZeros()
    {
        var summary = StatisticsAggregator.Summarize([], periodDays: null);

        Assert.Empty(summary.Categories);
        Assert.Equal(0, summary.BytesFreed);
        Assert.Equal(0, summary.Failures);
        Assert.Equal(0, summary.ScanCount);
    }

    [Fact]
    public void Summarize_Period_FiltersOldEvents()
    {
        var now = DateTime.Now;
        var events = new List<HistoryEvent>
        {
            Event(now.AddDays(-2), "Очистка"),
            Event(now.AddDays(-10), "Очистка"),
            Event(now.AddDays(-40), "Службы")
        };

        var week = StatisticsAggregator.Summarize(events, 7);
        var month = StatisticsAggregator.Summarize(events, 30);
        var all = StatisticsAggregator.Summarize(events, null);

        Assert.Equal(1, week.Categories.Sum(category => category.Operations));
        Assert.Equal(2, month.Categories.Sum(category => category.Operations));
        Assert.Equal(3, all.Categories.Sum(category => category.Operations));
    }

    [Fact]
    public void Summarize_BytesFreed_SumsMeasuredBytesOnly()
    {
        var now = DateTime.Now;
        var events = new List<HistoryEvent>
        {
            Event(now.AddHours(-1), "Очистка", bytesFreed: 1024),
            Event(now.AddHours(-2), "Очистка", bytesFreed: 2048),
            // BytesFreed null (корзина/кэш обновлений) — в сумму не входит и не ломает её.
            Event(now.AddHours(-3), "Очистка")
        };

        var summary = StatisticsAggregator.Summarize(events, null);

        Assert.Equal(3072, summary.BytesFreed);
    }

    [Fact]
    public void Summarize_Failures_CountStatusFail()
    {
        var now = DateTime.Now;
        var events = new List<HistoryEvent>
        {
            Event(now.AddMinutes(-5), "Службы"),
            Event(now.AddMinutes(-4), "Службы", HistoryEvent.StatusFail),
            Event(now.AddMinutes(-3), "Автозагрузка", HistoryEvent.StatusFail)
        };

        var summary = StatisticsAggregator.Summarize(events, null);

        Assert.Equal(2, summary.Failures);
    }

    [Fact]
    public void Summarize_ScanCategory_ExcludedFromOperationsButCountedSeparately()
    {
        var now = DateTime.Now;
        var events = new List<HistoryEvent>
        {
            Event(now.AddMinutes(-10), "Состояние ПК"),
            Event(now.AddMinutes(-9), "Состояние ПК", HistoryEvent.StatusFail),
            Event(now.AddMinutes(-8), "Очистка")
        };

        var summary = StatisticsAggregator.Summarize(events, null);

        // Проверки ПК не входят в операции: единственная категория — «Очистка».
        var category = Assert.Single(summary.Categories);
        Assert.Equal("Очистка", category.Category);
        Assert.Equal(1, category.Operations);
        Assert.Equal(2, summary.ScanCount);
        // Ошибка сканирования входит в общий счётчик ошибок периода.
        Assert.Equal(1, summary.Failures);
    }

    [Fact]
    public void Summarize_EnglishScanCategory_AlsoTreatedAsScan()
    {
        // Категория пишется в языке активного интерфейса при записи события.
        var events = new List<HistoryEvent> { Event(DateTime.Now, "PC Status") };

        var summary = StatisticsAggregator.Summarize(events, null);

        Assert.Empty(summary.Categories);
        Assert.Equal(1, summary.ScanCount);
    }

    [Fact]
    public void Summarize_GroupsCategoriesByStoredName()
    {
        var now = DateTime.Now;
        var events = new List<HistoryEvent>
        {
            Event(now.AddMinutes(-3), "Очистка"),
            Event(now.AddMinutes(-2), "Очистка"),
            Event(now.AddMinutes(-1), "Службы")
        };

        var summary = StatisticsAggregator.Summarize(events, null);

        Assert.Equal(2, summary.Categories.Count);
        // Сортировка: больше операций — выше, при равенстве — по алфавиту.
        Assert.Equal("Очистка", summary.Categories[0].Category);
        Assert.Equal(2, summary.Categories[0].Operations);
        Assert.Equal("Службы", summary.Categories[1].Category);
        Assert.Equal(1, summary.Categories[1].Operations);
    }
}
