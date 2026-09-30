using System.Diagnostics;
using SCU.Common;

namespace SCU.Views.Controls;

// Реализация shell-абстракций п. 13 аудита: вся работа с Process.Start и
// диалогами Microsoft.Win32 сосредоточена в presentation-слое.
public sealed class ShellOpenService : IShellOpenService
{
    public void OpenPath(string pathOrUrl)
    {
        Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
    }

    public void ShowInExplorer(string filePath)
    {
        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
    }

    public void OpenFolder(string folder)
    {
        Process.Start("explorer.exe", folder);
    }
}

public sealed class FilePickerService : IFilePickerService
{
    public string? PickOpenFile(string title, string? filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Title = title,
            Filter = filter ?? string.Empty,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickOpenFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string suggestedName, string? initialDirectory)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(suggestedName),
            InitialDirectory = initialDirectory,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
