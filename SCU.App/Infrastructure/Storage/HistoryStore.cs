using System.Threading.Channels;
using SCU.Common;
using SCU.Models;

namespace SCU.Infrastructure.Storage;

// Хранилище истории операций: %AppData%\SCU\state\history.json.
// Append с ограничением 500 событий; та же стратегия восстановления, что у SnapshotStore.
//
// П. 12 аудита: запись из UI идёт через Enqueue в контролируемую фоновую
// очередь (один писатель, строгий порядок) вместо fire-and-forget задач;
// при завершении приложения App.OnExit вызывает FlushPendingAsync.
public sealed class HistoryStore
{
    private const int Capacity = 500;

    // П.24: записи старше 14 дней удаляются при записи и при загрузке
    // (Capacity=500 остаётся ограничением по количеству).
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private static readonly Channel<(HistoryStore Store, HistoryEvent Event)> Pending =
        Channel.CreateUnbounded<(HistoryStore, HistoryEvent)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private static readonly Task WriterLoop = Task.Run(DrainAsync);

    private static async Task DrainAsync()
    {
        try
        {
            await foreach (var (store, @event) in Pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // RecordAsync логирует сбои записи сама и никогда не бросает.
                await store.RecordAsync(@event).ConfigureAwait(false);
            }
        }
        catch
        {
            // Канал завершён при flush — норма на выключении.
        }
    }

    // Контролируемый flush при завершении приложения; не блокирует дольше timeout.
    public static async Task FlushPendingAsync(TimeSpan timeout)
    {
        Pending.Writer.TryComplete();
        try
        {
            await WriterLoop.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (Exception)
        {
        }
    }

    private readonly Logger _logger;
    private readonly JsonStateFile<HistoryEvent> _file;

    public HistoryStore(Logger logger, string? filePath = null)
    {
        _logger = logger;
        _file = new JsonStateFile<HistoryEvent>(
            logger,
            filePath ?? DefaultPath(),
            Capacity,
            "HISTORY",
            @event => @event.Timestamp,
            MaxAge);
    }

    public static string DefaultPath() => Path.Combine(DashboardStatePaths.Directory, "history.json");

    // Постановка события в фоновую очередь: не блокирует UI-поток, порядок
    // сохраняется, ошибки записи логируются писателем.
    public void Enqueue(HistoryEvent @event)
    {
        if (!Pending.Writer.TryWrite((this, @event)))
        {
            _logger.Warn("HISTORY | enqueue failed | очередь закрыта (flush при выходе)");
        }
    }

    // Запись события best-effort: сбой хранилища не влияет на саму операцию.
    // Глотает исключения сама: вызывается фоновым писателем очереди —
    // исключение иначе терялось до финализатора GC.
    public async Task RecordAsync(HistoryEvent @event, CancellationToken ct = default)
    {
        try
        {
            await _file.AppendAsync(@event, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Осознанная отмена — не ошибка.
        }
        catch (Exception exception)
        {
            _logger.Warn("HISTORY | record failed | " + exception.Message);
        }
    }

    // Полная история (раздел «История» и статистика Dashboard). Порядок — по возрастанию
    // Timestamp, как у LoadRecentAsync.
    public Task<IReadOnlyList<HistoryEvent>> LoadAllAsync(CancellationToken ct = default) =>
        _file.LoadAllAsync(ct);

    public Task<IReadOnlyList<HistoryEvent>> LoadRecentAsync(int max, CancellationToken ct = default) =>
        InternalLoadRecentAsync(max, ct);

    private async Task<IReadOnlyList<HistoryEvent>> InternalLoadRecentAsync(int max, CancellationToken ct)
    {
        var all = await _file.LoadAllAsync(ct).ConfigureAwait(false);
        return all
            .OrderBy(@event => @event.Timestamp)
            .TakeLast(Math.Max(0, max))
            .ToList();
    }
}
