using System.Windows;
using System.Windows.Controls;
using SCU.ViewModels.Sections;

namespace SCU.Views.Sections;

// Раздел 22 «Браузер»: карточка запуска. Состояние ENTRY/BROWSER переключается
// биндингами по IsBrowserStarted; оболочка браузера живёт в отдельном окне
// (Views/BrowserWindow), которое создаётся лениво через HostFactory и
// показывается по ShowWindowRequested после успешного запуска.
public partial class BrowserView : UserControl
{
    private BrowserViewModel? ViewModel => DataContext as BrowserViewModel;

    public BrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is BrowserViewModel oldVm)
        {
            oldVm.HostFactory = null;
        }

        if (e.NewValue is BrowserViewModel newVm)
        {
            // Окно создаётся при первом «Запустить»; до этого существует скрытым.
            newVm.HostFactory = () => new BrowserWindow { DataContext = newVm };
        }
    }
}
