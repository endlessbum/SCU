using System.Windows;
using System.Windows.Media.Animation;
using SCU.Common;
using SCU;

namespace SCU.Views.Controls;

// Диалог подтверждения в виде системного ContentDialog: оверлей, карточка
// 452 px с радиусом 8, акцентная кнопка подтверждения. Прежнее размытие
// RootBorder удалено вместе со «плавающим» каркасом главного окна.
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string? confirmText = null)
    {
        InitializeComponent();
        Title = title;
        TitleText = title;
        MessageText = message;
        // Дефолт — из словаря строк: при смене языка кнопка переводится вместе с UI
        ConfirmButton.Content = confirmText
            ?? Application.Current?.TryFindResource("S_DialogConfirm") as string
            ?? L.T("Подтвердить");
        DataContext = this;
        Loaded += (_, _) =>
        {
            var storyboard = (Storyboard)Resources["AppearStoryboard"];
            storyboard.Begin(this);
        };
    }

    public string TitleText { get; }

    public string MessageText { get; }

    public static bool Ask(string title, string message, string? confirmText = null)
    {
        var dialog = new ConfirmDialog(title, message, confirmText)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow
        };

        // Тёмная подложка убрана: фон главного окна размывается на время диалога.
        var owner = dialog.Owner as MainWindow;
        owner?.SetContentBlur(true);
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally
        {
            owner?.SetContentBlur(false);
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
