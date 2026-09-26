using System.Windows;
using System.Windows.Controls;
using SCU.ViewModels.Sections;

namespace SCU.Views.Sections;

public partial class InfoView : UserControl
{
    public InfoView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is InfoViewModel vm)
        {
            if (vm.RefreshCommand.CanExecute(null) && !vm.IsLoading && vm.SystemInfo == null)
            {
                vm.RefreshCommand.Execute(null);
            }
        }
    }
}