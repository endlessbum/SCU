using System.Windows;
using SCU.Common;
using SCU.Services;

namespace SCU.Views;

// Встроенный редактор пользовательского скрипта: «Посмотреть» — только чтение,
// «Редактировать» — правка с сохранением и повторной проверкой работоспособности.
public partial class ScriptEditorWindow : Window
{
    private readonly UserScriptStore _store;
    private readonly UserScriptData _data;

    public string ScriptTitle => _data.Title;

    public bool IsReadOnly { get; init; }

    public string StatusText { get; set; } = string.Empty;

    public ScriptEditorWindow(UserScriptStore store, UserScriptData data, bool isReadOnly)
    {
        InitializeComponent();
        _store = store;
        _data = data;
        IsReadOnly = isReadOnly;
        ScriptBox.Text = File.Exists(store.ScriptPath(data))
            ? File.ReadAllText(store.ScriptPath(data))
            : string.Empty;
        SaveButton.Visibility = isReadOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSaveApply(object sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(_store.ScriptPath(_data), ScriptBox.Text);
        }
        catch (Exception exception)
        {
            StatusText = L.T("Не удалось сохранить: {0}", exception.Message);
            return;
        }

        // Сохранение = применение: повторная проверка работоспособности (без запуска).
        var validation = _store.ValidateAsync(_store.ScriptPath(_data)).GetAwaiter().GetResult();
        StatusText = validation.Ok
            ? L.T("Скрипт сохранён и проверен — рабочий.")
            : L.T("Скрипт сохранён, но проверка не пройдена: {0}", validation.Message);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
