namespace SCU.Models;

// Событие истории операций (раздел «Состояние ПК», список «Последние события»).
// Status — "ok"/"fail" (константы ниже; строка вместо enum — ради читаемого JSON).
// Опциональные поля этапа 2: BytesFreed — реально измеренные байты очистки,
// BackupId/SnapshotId — идентификаторы резерва/точки восстановления (заполняются
// только когда источник отдаёт надёжный идентификатор; иначе остаются null).
// Старые JSON-файлы читаются: System.Text.Json подставляет default для отсутствующих полей.
public sealed record HistoryEvent(
    DateTime Timestamp,
    string Category,
    string Operation,
    string Status,
    string? Details = null,
    long? BytesFreed = null,
    string? BackupId = null,
    string? SnapshotId = null)
{
    public const string StatusOk = "ok";
    public const string StatusFail = "fail";
}
