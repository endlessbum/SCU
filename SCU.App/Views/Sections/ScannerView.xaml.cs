using System.Windows;
using SCU.ViewModels.Sections;

namespace SCU.Views.Sections;

// Раздел 21 «Сканер». Code-behind только для drag-and-drop (документ п. 27):
// перетаскивание файла/папки в drop-зону или на вкладку запускает скан
// немедленно. Вся остальная логика — в ScannerViewModel.
public partial class ScannerView
{
    public ScannerView()
    {
        InitializeComponent();
    }

    private ScannerViewModel? ViewModel => DataContext as ScannerViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && ViewModel?.IsInteractive == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            Opacity = 0.85;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        Opacity = 1;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        Opacity = 1;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel is not null)
        {
            await ViewModel.HandleDroppedPathsAsync(paths);
        }
    }
}
