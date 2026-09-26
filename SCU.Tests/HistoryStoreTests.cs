using SCU.Common;
using SCU.Models;
using SCU.Services.Dashboard;
using Xunit;

namespace SCU.Tests;

// HistoryStore: запись/чтение событий, вытеснение старых записей (cap 500)
// и восстановление после повреждения файла. Файл — во временной папке теста.
public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;
    private readonly Logger _logger;
    private readonly HistoryStore _store;

    public HistoryStoreTests()
    {
        // Уникальная временная папка на каждый тест; удаление — в Dispose.
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "history.json");
        _logger = Logger.CreateForCurrentRun();
        _store = new HistoryStore(_logger, _filePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична: сбой удаления не роняет тест.
        }
    }

    private static HistoryEvent Event(int index, DateTime timestamp, string status = HistoryEvent.StatusOk) =>
        new(timestamp, "Категория-" + index, "Операция-" + index, status, "Детали-" + index);

    [Fact]
    public async Task RecordAsync_LoadRecentAsync_RoundtripPreservesFields()
    {
        // П.24: хранилище режет записи старше 14 дней — метки времени в тестах
        // должны быть свежими (относительно DateTime.Now).
        var @event = new HistoryEvent(
            DateTime.Now,
            "Состояние ПК",
            "Проверка ПК",
            HistoryEvent.StatusOk,
            "Областей с ошибками: 0");

        await _store.RecordAsync(@event);
        var loaded = await _store.LoadRecentAsync(10);

        var restored = Assert.Single(loaded);
        Assert.Equal(@event, restored);
    }

    [Fact]
    public async Task LoadRecentAsync_KeepsEventsSortedByTimestamp()
    {
        // Запись вперемешку: порядок чтения определяется Timestamp, а не порядком записи.
        var baseTime = DateTime.Now;
        var saved = new List<HistoryEvent>();
        for (var i = 0; i < 5; i++)
        {
            var index = 4 - i; // новейшее событие записывается первым
            var @event = Event(index, baseTime.AddMinutes(index));
            saved.Add(@event);
            await _store.RecordAsync(@event);
        }

        var loaded = await _store.LoadRecentAsync(10);

        // Хранилище отдаёт хронологический (возрастающий) порядок; новейшее — в конце.
        // Раздел Dashboard переворачивает список для отображения (новейшие сверху).
        Assert.Equal(5, loaded.Count);
        Assert.Equal(loaded.OrderBy(e => e.Timestamp), loaded);
        Assert.Equal(saved.Max(e => e.Timestamp), loaded[^1].Timestamp);
        Assert.Equal(saved.Min(e => e.Timestamp), loaded[0].Timestamp);
    }

    [Fact]
    public async Task LoadRecentAsync_ReturnsOnlyRequestedMaxOfNewest()
    {
        var baseTime = DateTime.Now;
        for (var i = 0; i < 10; i++)
        {
            await _store.RecordAsync(Event(i, baseTime.AddMinutes(i)));
        }

        var loaded = await _store.LoadRecentAsync(3);

        Assert.Equal(3, loaded.Count);
        // Взяты последние (самые новые) события: 7, 8, 9 по возрастанию.
        Assert.Equal(baseTime.AddMinutes(7), loaded[0].Timestamp);
        Assert.Equal(baseTime.AddMinutes(9), loaded[^1].Timestamp);
    }

    [Fact]
    public async Task RecordAsync_OverCapacity_KeepsOnlyNewest500()
    {
        // 520 событий с шагом в секунду — все внутри 14 дней, работает только лимит ёмкости.
        var baseTime = DateTime.Now;
        var saved = new List<HistoryEvent>();
        for (var i = 0; i < 520; i++)
        {
            var @event = Event(i, baseTime.AddSeconds(i));
            saved.Add(@event);
            await _store.RecordAsync(@event);
        }

        var loaded = await _store.LoadRecentAsync(500);

        Assert.Equal(500, loaded.Count);
        // Первые 20 (самые старые) вытеснены; порядок — по возрастанию Timestamp.
        Assert.DoesNotContain(loaded, e => e.Timestamp == saved[0].Timestamp);
        Assert.Equal(saved[20], loaded[0]);
        Assert.Equal(saved[^1], loaded[^1]);
    }

    [Fact]
    public async Task LoadRecentAsync_CorruptedFile_ReturnsEmptyAndMovesFileAside()
    {
        await File.WriteAllTextAsync(_filePath, "не json вовсе");

        var loaded = await _store.LoadRecentAsync(10);

        Assert.Empty(loaded);
        Assert.False(File.Exists(_filePath));
        // Повреждённый файл сохранён рядом под именем *.corrupt-<timestamp>.
        Assert.NotEmpty(Directory.GetFiles(_directory, "history.json.corrupt-*"));
    }

    [Fact]
    public async Task RecordAsync_EventsOlderThan14Days_Trimmed()
    {
        // П.24: записи за 16 дней — при записи остаются только последние 14 дней.
        var now = DateTime.Now;
        var saved = new List<HistoryEvent>();
        for (var i = 0; i < 17; i++)
        {
            var @event = Event(i, now.AddDays(-16 + i)); // от -16 дней до текущего момента
            saved.Add(@event);
            await _store.RecordAsync(@event);
        }

        var loaded = await _store.LoadAllAsync();

        // Самые старые (16 и 15 дней) вытеснены; запас в сутки — чтобы не зависеть
        // от микросекундной разницы DateTime.Now в тесте и хранилище.
        Assert.DoesNotContain(loaded, e => e.Timestamp == saved[0].Timestamp);
        Assert.DoesNotContain(loaded, e => e.Timestamp == saved[1].Timestamp);
        // Свежие записи (последние 14 дней) сохранены, порядок — по возрастанию.
        Assert.Contains(loaded, e => e.Timestamp == saved[3].Timestamp);
        Assert.Equal(saved[^1], loaded[^1]);
        Assert.All(loaded, e => Assert.True(e.Timestamp >= DateTime.Now.AddDays(-14).AddSeconds(-1)));
    }

    [Fact]
    public async Task LoadAllAsync_OldEventsInFile_TrimmedOnLoad()
    {
        // П.24: обрезка выполняется и при загрузке — старые записи из файла не отдаются.
        var now = DateTime.Now;
        var old = new List<HistoryEvent>
        {
            Event(1, now.AddDays(-30)),
            Event(2, now.AddDays(-20)),
            Event(3, now.AddDays(-1)),
            Event(4, now.AddHours(-1))
        };
        await File.WriteAllTextAsync(
            _filePath,
            System.Text.Json.JsonSerializer.Serialize(old));

        var loaded = await _store.LoadAllAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(old[2], loaded[0]);
        Assert.Equal(old[3], loaded[^1]);
    }
}
