using System.Windows;
using System.Windows.Input;
using SCU.ViewModels;

namespace SCU.Views.Controls;

// Окно безопасного просмотра. Code-behind только для двойного клика по списку
// папки; вся логика чтения — в SafeViewerViewModel (только данные, п. 22).
public partial class SafeViewerWindow
{
    public SafeViewerWindow(SafeViewerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private SafeViewerViewModel? ViewModel => DataContext as SafeViewerViewModel;

    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        // Двойной клик — там, где кликнули: SourceItem надёжнее SelectedItem.
        if (e.OriginalSource is FrameworkElement element && element.DataContext is SafeViewerEntry entry)
        {
            ViewModel.OpenEntryCommand.Execute(entry);
        }
    }
}
