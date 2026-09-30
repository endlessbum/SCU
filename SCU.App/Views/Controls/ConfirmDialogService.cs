using SCU.Common;

namespace SCU.Views.Controls;

public sealed class ConfirmDialogService : IConfirmDialogService
{
    public bool Ask(string title, string message, string? confirmText = null)
    {
        // Тумблер «UAC» в настройках выключен — подтверждения рисковых действий
        // не показываются, операция считается подтверждённой сразу.
        if (!SecurityPrompts.Enabled)
        {
            return true;
        }

        return ConfirmDialog.Ask(title, message, confirmText);
    }

    // П. 14 аудита: структурированный флоу опасного изменения — отдельные
    // маркированные блоки «Сейчас / Станет / Последствия / Откат» вместо
    // свободного текста, кнопка подтверждения называется действием.
    public bool ConfirmChange(DestructiveChange change)
    {
        if (!SecurityPrompts.Enabled)
        {
            return true;
        }

        var rollback = change.Rollback ?? L.T("нет — действие необратимо");
        var message = string.Join(
            "\n\n",
            "⚠ " + L.T("Рискованное изменение"),
            L.T("Сейчас") + ":\n" + change.CurrentState,
            L.T("Станет") + ":\n" + change.NewState,
            L.T("Последствия") + ":\n" + change.Consequences,
            L.T("Откат") + ": " + rollback);

        return ConfirmDialog.Ask(change.Title, message, change.ConfirmText);
    }
}
