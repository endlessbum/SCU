namespace SCU.Common;

// Абстракции shell-взаимодействия для ViewModel-слоя (п. 13 аудита): VM не
// знают Process.Start и WPF-диалоги Microsoft.Win32 — только эти интерфейсы.
// Реализации живут рядом с ConfirmDialogService в presentation-слое.

// Запуск ассоциированного обработчика системой (файл/URL/папка/показ в проводнике).
public interface IShellOpenService
{
    // Открыть файл или URL обработчиком по умолчанию (UseShellExecute).
    void OpenPath(string pathOrUrl);

    // Показать файл в проводнике с выделением.
    void ShowInExplorer(string filePath);

    // Открыть папку в проводнике.
    void OpenFolder(string folder);
}

// Файловые диалоги выбора. null — пользователь отменил выбор.
public interface IFilePickerService
{
    // Выбор существующего файла; filter — строка формата OpenFileDialog
    // («Описание|*.ext|…»), null — без фильтра.
    string? PickOpenFile(string title, string? filter);

    // Выбор существующей папки.
    string? PickOpenFolder(string title);

    // Диалог «куда сохранить»; suggestedName задаёт имя/расширение по умолчанию.
    string? PickSaveFile(string suggestedName, string? initialDirectory);
}
