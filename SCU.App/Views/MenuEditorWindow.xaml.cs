using System.Windows;
using SCU.ViewModels;

namespace SCU.Views;

// Окно «Редактирование меню» (открывается из карточки настроек): изменения
// применяются сразу к живой MainViewModel и сохраняются в menu.json.
public partial class MenuEditorWindow : Window
{
    public MenuEditorWindow(MainViewModel main, SCU.Common.Logger logger)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;
        DataContext = new MenuEditorViewModel(main, logger);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
