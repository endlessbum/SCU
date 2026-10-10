using System.Windows;
using SCU.Common;

namespace SCU.Views;

// Встроенный редактор пользовательского скрипта: «Посмотреть» — только чтение,
// «Редактировать» — правка с сохранением и повторной проверкой работоспособности.
public partial class ScriptEditorWindow : Window
{
    private readonly UserScriptStore _store;
    private readonly UserScriptData _data;

    public string ScriptTitle => _data.Title;

    public bool IsReadOnly { get; init; }

    // DependencyProperty, а не CLR-свойство (H-04): биндинг XAML получает
    // уведомления только от свойств с механизмом изменений — обычное CLR-свойство
    // окна обновляло значение, но UI оставался со старым текстом статуса.
    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(ScriptEditorWindow), new PropertyMetadata(string.Empty));

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public ScriptEditorWindow(UserScriptStore store, UserScriptData data, bool isReadOnly)
    {
        InitializeComponent();
        // Появление окна: fade + подъём корня (единый паттерн ConfirmDialog/OnboardingWindow).
        Loaded += (_, _) => SCU.Common.UiAnimations.AppearWindow(this);
        _store = store;
        _data = data;
        IsReadOnly = isReadOnly;
        ScriptBox.Text = File.Exists(store.ScriptPath(data))
            ? File.ReadAllText(store.ScriptPath(data))
            : string.Empty;
        SaveButton.Visibility = isReadOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnSaveApply(object sender, RoutedEventArgs e)
    {
        // UpdateContent пишет файл, пересчитывает эталонный SHA-256 и сохраняет
        // scripts.json: без пересчёта следующий запуск отменялся бы по
        // целостности (файл = новый хэш, метаданные = старый).
        if (!_store.UpdateContent(_data, ScriptBox.Text))
        {
            StatusText = L.T("Не удалось сохранить: список скриптов не записан, попробуйте ещё раз.");
            return;
        }

        // Сохранение = применение: повторная проверка работоспособности (без запуска).
        // ValidateAsync запускает powershell.exe — async void-обработчик с await,
        // иначе UI-поток замерзает на весь запуск и парсинг.
        try
        {
            var validation = await _store.ValidateAsync(_store.ScriptPath(_data)).ConfigureAwait(true);
            StatusText = validation.Ok
                ? L.T("Скрипт сохранён и проверен — рабочий.")
                : L.T("Скрипт сохранён, но проверка не пройдена: {0}", validation.Message);
        }
        catch (Exception exception)
        {
            StatusText = L.T("Скрипт сохранён, но проверка не удалась: {0}", exception.Message);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
