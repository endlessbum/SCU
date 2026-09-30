using System.Diagnostics.Eventing.Reader;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Ошибки системных журналов за 7 дней. Собираем только поставщиков из списка
/// наблюдения (п. 7.6) с дедупликацией: N одинаковых событий → одна запись с Count.
/// </summary>
public sealed class EventLogProbe : DiagnosticProbe
{
    private const int WindowDays = 7;
    private const int MaxEventsPerLog = 2000;

    private static readonly IReadOnlyDictionary<string, string[]> WatchList = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        // [журнал]: поставщики (пустой массив — все с Level 1/2, но с фильтром по id ниже).
        ["System"] =
        [
            "BugCheck", "Kernel-Power", "Disk", "Ntfs", "volmgr", "WHEA-Logger",
            "Service Control Manager", "Microsoft-Windows-WindowsUpdateClient"
        ],
        ["Application"] = ["Application Error", "Windows Error Reporting", ".NET Runtime"],
    };

    // Service Control Manager шумит: берём только известные коды отказов служб.
    private static readonly int[] ScmEventIds = [7000, 7009, 7011, 7024, 7031, 7034, 7043];

    public override string Id => "events";

    public override string Title => "Проверка событий";

    public override Task CollectAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() =>
    {
        var summaries = new List<EventErrorSummary>();
        try
        {
            foreach (var (logName, providers) in WatchList)
            {
                ct.ThrowIfCancellationRequested();
                summaries.AddRange(ReadLog(logName, providers, ct));
            }

            context.EventLogReadFailed = false;
            context.EventErrors = summaries
                .OrderByDescending(summary => summary.Count)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Журнал может быть недоступен (права, политика) — это не сбой запуска.
            context.EventLogReadFailed = true;
        }
    }, CancellationToken.None);

    private static IEnumerable<EventErrorSummary> ReadLog(string logName, IReadOnlyList<string> providers, CancellationToken ct)
    {
        // Level 1 (Critical) и 2 (Error) за окно наблюдения.
        var query = new EventLogQuery(logName, PathType.LogName,
            $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {WindowDays * 24L * 60 * 60 * 1000}]]]");
        using var reader = new EventLogReader(query);
        var providerSet = providers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aggregates = new Dictionary<(string Provider, int Id), (int Count, DateTime? Latest)>();
        var read = 0;

        while (read < MaxEventsPerLog)
        {
            ct.ThrowIfCancellationRequested();
            EventRecord record;
            try
            {
                record = reader.ReadEvent();
            }
            catch (EventLogNotFoundException)
            {
                yield break;
            }

            if (record is null)
            {
                break;
            }

            read++;
            using (record)
            {
                var provider = record.ProviderName ?? string.Empty;
                if (!providerSet.Contains(provider))
                {
                    continue;
                }

                if (provider.Equals("Service Control Manager", StringComparison.OrdinalIgnoreCase)
                    && !ScmEventIds.Contains(record.Id))
                {
                    continue;
                }

                var key = (provider, record.Id);
                if (aggregates.TryGetValue(key, out var existing))
                {
                    aggregates[key] = (existing.Count + 1, existing.Latest);
                }
                else
                {
                    DateTime? latest = null;
                    try
                    {
                        latest = record.TimeCreated;
                    }
                    catch
                    {
                    }

                    aggregates[key] = (1, latest);
                }
            }
        }

        foreach (var ((provider, id), (count, latest)) in aggregates)
        {
            yield return new EventErrorSummary(provider, id, logName, count, latest);
        }
    }
}
