using System.Windows;
using System.Windows.Controls;
using SCU.Common;
using SCU.ViewModels;

namespace SCU.Views;

// Окно «Установленные» пользовательские скрипты: Просмотр (только чтение),
// Редактировать (с сохранением и применением), Удалить (файл + карточка).
public partial class InstalledScriptsWindow : Window
{
    private readonly MainViewModel _main;
    private readonly UserScriptStore _store;
    private readonly Logger _logger;

    public InstalledScriptsWindow(MainViewModel main, Logger logger)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;
        _main = main;
        _logger = logger;
        _store = new UserScriptStore(logger);
        Reload();
    }

    private static UserScriptData? ScriptFromButton(object sender)
    {
        var context = (sender as FrameworkElement)?.DataContext;
        var property = context?.GetType().GetProperty("Data");
        return property?.GetValue(context) as UserScriptData;
    }

    private void Reload()
    {
        var sectionsByNumber = _main.Sections.ToDictionary(section => section.Number, section => section.Title);
        ScriptsList.ItemsSource = _store.Load()
            .Select(data => new
            {
                Data = data,
                data.Title,
                data.Comment,
                PlacementText = L.T("Раздел: {0}", sectionsByNumber.GetValueOrDefault(data.SectionNumber, "?")),
            })
            .ToList();
    }

    private void OnViewClick(object sender, RoutedEventArgs e)
    {
        if (ScriptFromButton(sender) is { } data)
        {
            new ScriptEditorWindow(_store, data, isReadOnly: true) { Owner = this }.ShowDialog();
        }
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (ScriptFromButton(sender) is { } data)
        {
            new ScriptEditorWindow(_store, data, isReadOnly: false) { Owner = this }.ShowDialog();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ScriptFromButton(sender) is not { } data)
        {
            return;
        }

        _main.RemoveUserScript(data.Id);
        Reload();
    }
}
