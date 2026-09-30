using SCU.Models;

namespace SCU.AppCore.Dashboard;

// Точка графика «Трендов»: реальное измерение (время, значение). Value — в тех
// единицах, что лежат в источнике: FreeGb для дисков, байты для очисток.
public sealed record DataPoint(DateTime Timestamp, double Value);

// Чистый построитель серий для графиков Dashboard (юнит-тестируемый). Никаких
// выдуманных значений: серия — только реально прочитанные точки, прореживание
// отбирает подмножество существующих точек, а не интерполирует новые.
public static class ChartDataBuilder
{
    // Серия «свободное место» по диску с заданной буквой. Снимки без этого диска
    // пропускаются; точек меньше двух — серии нет (график из одной точки бессмыслен).
    // Буква нормализуется: DiskSnapshot хранит "C:", а вызывающий может передать "C:"/"C".
    public static IReadOnlyList<DataPoint> FreeDiskSpaceSeries(
        IReadOnlyList<SystemSnapshot> snapshots,
        string diskLetter,
        int maxPoints)
    {
        if (maxPoints < 1 || string.IsNullOrWhiteSpace(diskLetter))
        {
            return [];
        }

        var letter = NormalizeLetter(diskLetter);
        var points = snapshots
            .OrderBy(snapshot => snapshot.Timestamp)
            .SelectMany(snapshot => snapshot.Disks
                .Where(disk => string.Equals(
                    NormalizeLetter(disk.Letter),
                    letter,
                    StringComparison.OrdinalIgnoreCase))
                .Select(disk => new DataPoint(snapshot.Timestamp, disk.FreeGb)))
            .ToList();

        return points.Count < 2 ? [] : ThinEvenly(points, maxPoints);
    }

    // Серия «освобождено очистками»: только события с реально измеренными байтами
    // (BytesFreed != null) категории «Очистка». Категория пишется в языке момента
    // записи, поэтому учитываются оба варианта (по образцу StatisticsAggregator).
    // Берутся последние maxBars события по времени.
    public static IReadOnlyList<DataPoint> CleanupBytesSeries(
        IReadOnlyList<HistoryEvent> events,
        int maxBars)
    {
        if (maxBars < 1)
        {
            return [];
        }

        return events
            .Where(@event => @event.BytesFreed is not null && IsCleanupCategory(@event.Category))
            .OrderBy(@event => @event.Timestamp)
            .TakeLast(maxBars)
            .Select(@event => new DataPoint(@event.Timestamp, @event.BytesFreed!.Value))
            .ToList();
    }

    // Равномерное прореживание: индексы, равномерно покрывающие ряд, включая первый
    // и последний. Совпадающие индексы (при округлении) схлопываются — значения
    // не дублируются и не выдумываются.
    private static IReadOnlyList<DataPoint> ThinEvenly(List<DataPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints)
        {
            return points;
        }

        var result = new List<DataPoint>(maxPoints);
        var step = (points.Count - 1) / (double)(maxPoints - 1);
        for (var i = 0; i < maxPoints; i++)
        {
            var index = (int)Math.Round(i * step);
            if (result.Count == 0 || result[^1].Timestamp != points[index].Timestamp)
            {
                result.Add(points[index]);
            }
        }

        return result;
    }

    private static string NormalizeLetter(string letter) => letter.TrimEnd(':', '\\').ToUpperInvariant();

    private static bool IsCleanupCategory(string category) =>
        string.Equals(category, "Очистка", StringComparison.Ordinal)
        || string.Equals(category, "Cleanup", StringComparison.Ordinal);
}
