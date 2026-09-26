using System.Text.Json;
using SCU.Models;
using Xunit;

namespace SCU.Tests;

// Обратная совместимость HistoryEvent: старый JSON (без BytesFreed/BackupId/SnapshotId)
// должен читаться — System.Text.Json подставляет default для отсутствующих полей.
// Новые поля сериализуются и читаются обратно (roundtrip).
public sealed class HistoryEventCompatTests
{
    [Fact]
    public void Deserialize_OldFormatWithoutNewFields_NewFieldsAreNull()
    {
        // Формат этапа 1: ровно пять полей.
        const string json = """
        [
          {
            "Timestamp": "2026-03-05T10:30:00",
            "Category": "Очистка",
            "Operation": "Временные файлы",
            "Status": "ok",
            "Details": "Удалено файлов: 12 (34.5 MB)."
          }
        ]
        """;

        var events = JsonSerializer.Deserialize<List<HistoryEvent>>(json);

        var restored = Assert.Single(events!);
        Assert.Equal(new DateTime(2026, 3, 5, 10, 30, 0), restored.Timestamp);
        Assert.Equal("Очистка", restored.Category);
        Assert.Equal("Временные файлы", restored.Operation);
        Assert.Equal(HistoryEvent.StatusOk, restored.Status);
        Assert.Equal("Удалено файлов: 12 (34.5 MB).", restored.Details);
        Assert.Null(restored.BytesFreed);
        Assert.Null(restored.BackupId);
        Assert.Null(restored.SnapshotId);
    }

    [Fact]
    public void Serialize_NewFields_RoundtripPreservesValues()
    {
        var @event = new HistoryEvent(
            new DateTime(2026, 9, 1, 8, 15, 0),
            "Очистка",
            "Кэш браузеров",
            HistoryEvent.StatusOk,
            "Удалено файлов: 40 (12.0 MB).",
            12_582_912,
            "backup-2026-09-01",
            "snapshot-1");

        var json = JsonSerializer.Serialize(@event);
        var restored = JsonSerializer.Deserialize<HistoryEvent>(json);

        Assert.Equal(@event, restored);
        Assert.Equal(12_582_912, restored!.BytesFreed);
        Assert.Equal("backup-2026-09-01", restored.BackupId);
        Assert.Equal("snapshot-1", restored.SnapshotId);
    }

    [Fact]
    public void Deserialize_OldFormatStatusFail_IsFail()
    {
        const string json = """
        [
          { "Timestamp": "2026-01-02T03:04:05", "Category": "Автозагрузка", "Operation": "Элемент", "Status": "fail" }
        ]
        """;

        var restored = Assert.Single(JsonSerializer.Deserialize<List<HistoryEvent>>(json)!);

        Assert.Equal(HistoryEvent.StatusFail, restored.Status);
        Assert.Null(restored.Details);
        Assert.Null(restored.BytesFreed);
    }
}
