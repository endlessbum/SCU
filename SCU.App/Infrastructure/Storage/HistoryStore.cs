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

    // Счётчик событий, ещё не записанных на диск (аудит flush-семантики):
    // инкремент в Enqueue, декремент в DrainAsync после фактической записи.
    // FlushPendingAsync ждёт именно нуля, а не завершения WriterLoop —
    // тот живёт до конца процесса и никогда не завершается.
    private static int _pendingCount;

    private static async Task DrainAsync()
    {
        try
        {
            await foreach (var (store, @event) in Pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // RecordAsync логирует сбои записи сама и никогда не бросает;
                // декремент — в finally, чтобы счётчик не залип при любом исходе.
                try
                {
                    await store.RecordAsync(@event).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
        }
        catch
        {
            // Канал завершён — норма на выключении (WriterLoop живёт до конца процесса).
        }
    }

    // Контролируемый flush при завершении приложения: ждёт, пока очередь ДОЙДЁТ
    // до нуля необработанных событий (гарантия записи), но не дольше timeout.
    // Возврат сразу после выписки — без лишней полной задержки; сам WriterLoop
    // продолжает жить: фоновые операции, доживающие до закрытия окна, дописывают
    // свои события после flush.
    public static async Task FlushPendingAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pendingCount) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
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
    // сохраняется, ошибки записи логируются писателем. Счётчик _pendingCount
    // инкрементируется до TryWrite (декремент при отказе канала).
    public void Enqueue(HistoryEvent @event)
    {
        Interlocked.Increment(ref _pendingCount);
        if (!Pending.Writer.TryWrite((this, @event)))
        {
            Interlocked.Decrement(ref _pendingCount);
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
