using System.Globalization;
using SCU.Common;
using SCU.Models;

namespace SCU.ViewModels.Sections;

// Строка события истории: используется разделом 18 «История» и шаблоном
// HistoryEventRow из Themes/Controls.xaml (общий с блоком «Последние события»).
// Формат колонок стабилен: время, категория, операция, значок успеха/ошибки.
public sealed class HistoryEventRow
{
    public HistoryEventRow(HistoryEvent @event)
    {
        TimeText = @event.Timestamp.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        Category = @event.Category;
        Operation = @event.Operation;
        // П. SCAN-01: warn — неполное покрытие (например, скан с пропусками);
        // это не ошибка, но и не «чистый успех», поэтому имеет свою метку.
        IsFail = string.Equals(@event.Status, HistoryEvent.StatusFail, StringComparison.Ordinal);
        IsWarn = string.Equals(@event.Status, HistoryEvent.StatusWarn, StringComparison.Ordinal);
        Details = @event.Details;
        // Пометка «бэкап: RP-12345» — только когда событие несёт надёжный идентификатор.
        BackupText = @event.BackupId is null ? null : L.T("Бэкап: {0}", @event.BackupId);
        StatusText = IsFail ? L.T("Ошибка") : IsWarn ? L.T("Неполное") : string.Empty;
    }

    public string TimeText { get; }

    public string Category { get; }

    public string Operation { get; }

    public bool IsFail { get; }

    // Неполное покрытие (п. SCAN-01): не ошибка, но и не «чистый успех».
    public bool IsWarn { get; }

    // Текстовая пометка статуса: «Ошибка» для fail, «Неполное» для warn.
    public string StatusText { get; }

    public string? Details { get; }

    public string? BackupText { get; }

    public bool HasBackup => BackupText is not null;
}
