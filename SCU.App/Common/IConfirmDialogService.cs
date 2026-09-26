namespace SCU.Common;

public interface IConfirmDialogService
{
    bool Ask(string title, string message, string? confirmText = null);
}
