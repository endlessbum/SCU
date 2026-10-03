using System.Windows;

namespace SCU.Views;

// Окно «Бэкапы»: все созданные бэкапы приложения — раздел, время создания
// и назначение каждого. Данные собирает BackupCatalog из %AppData%\SCU\backup.
public partial class BackupsWindow : Window
{
    public BackupsWindow()
    {
        InitializeComponent();
        DataContext = AppCore.BackupCatalog.Collect();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
