using System.Text.Json;
using SCU.Infrastructure.Logging;
using SCU.Infrastructure.Storage;
using SCU.Models;
using Xunit;

namespace SCU.Tests;

// Аудит flush-семантики истории: после FlushPendingAsync событие гарантированно
// записано в history.json (flush ждёт обнуления очереди, а не «полного таймаута»).
public sealed class HistoryStoreFlushTests : IDisposable
{
    private readonly string _directory;

    public HistoryStoreFlushTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична.
        }
    }

    [Fact]
    public async Task Enqueue_Flush_EventIsOnDisk()
    {
        var path = Path.Combine(_directory, "history.json");
        var store = new HistoryStore(Logger.CreateForCurrentRun(), path);
        var marker = "SCU-FLUSH-MARKER-" + Guid.NewGuid().ToString("N");

        store.Enqueue(new HistoryEvent(DateTime.Now, "Тест", marker, HistoryEvent.StatusOk, "audit flush"));
        await HistoryStore.FlushPendingAsync(TimeSpan.FromSeconds(10));

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains(marker, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flush_EmptyQueue_ReturnsPromptly()
    {
        // Пустая очередь: flush не должен выжидать полный таймаут.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await HistoryStore.FlushPendingAsync(TimeSpan.FromMinutes(5));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "flush на пустой очереди должен возвращаться сразу");
    }
}
