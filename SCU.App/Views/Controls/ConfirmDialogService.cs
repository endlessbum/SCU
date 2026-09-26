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
}
