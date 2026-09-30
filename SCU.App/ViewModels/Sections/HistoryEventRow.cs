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
        IsFail = !string.Equals(@event.Status, HistoryEvent.StatusOk, StringComparison.Ordinal);
        Details = @event.Details;
        // Пометка «Резерв: RP-12345» — только когда событие несёт надёжный идентификатор.
        BackupText = @event.BackupId is null ? null : L.T("Резерв: {0}", @event.BackupId);
        StatusText = IsFail ? L.T("Ошибка") : string.Empty;
    }

    public string TimeText { get; }

    public string Category { get; }

    public string Operation { get; }

    public bool IsFail { get; }

    // Текстовая пометка ошибки (успешные операции пометки не имеют).
    public string StatusText { get; }

    public string? Details { get; }

    public string? BackupText { get; }

    public bool HasBackup => BackupText is not null;
}
