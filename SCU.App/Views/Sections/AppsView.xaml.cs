using System.Windows;
using System.Windows.Controls;
using SCU.ViewModels.Sections;

namespace SCU.Views.Sections;

public partial class AppsView : UserControl
{
    public AppsView()
    {
        InitializeComponent();

        // Активность поиска «Приложений»: затемнение фона окна — как у поиска
        // на «Главной» (состояние живёт в AppsViewModel, читает MainWindow).
        SearchBox.GotKeyboardFocus += (_, _) => AppsVm().IsSearchFocused = true;
        SearchBox.LostKeyboardFocus += (_, _) => AppsVm().IsSearchFocused = false;
    }

    private AppsViewModel AppsVm() => (AppsViewModel)DataContext;
}
