namespace SCU.Common;

public interface IConfirmDialogService
{
    bool Ask(string title, string message, string? confirmText = null);

    // П. 14 аудита: подтверждение опасного изменения со структурированным
    // флоу «сейчас -> станет -> последствия -> откат». Реализация обязана
    // показать все поля явно, не смешивая их в один абзац.
    bool ConfirmChange(DestructiveChange change) => Ask(
        change.Title,
        L.T("Сейчас:\n{0}\n\n{1}:\n{2}\n\n{3}:\n{4}\n\n{5}: {6}",
            change.CurrentState,
            L.T("Станет"), change.NewState,
            L.T("Последствия"), change.Consequences,
            L.T("Откат"), change.Rollback ?? L.T("нет — действие необратимо")),
        change.ConfirmText);
}
