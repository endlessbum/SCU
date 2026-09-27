using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SCU.Common;

namespace SCU.Views.Controls;

// Отображение центра уведомлений (AppNotificationCenter) в MainWindow:
// вертикальный стек тостов в правом верхнем углу.
public partial class NotificationHost : UserControl
{
    public NotificationHost()
    {
        InitializeComponent();
        DataContext = AppNotificationCenter.Instance;
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppNotification notification)
        {
            AppNotificationCenter.Instance.Dismiss(notification);
        }
    }

    // Действие тоста (например, страница релизов) — системный браузер.
    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppNotification { ActionUrl: { } url } notification)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                AppNotificationCenter.Instance.Dismiss(notification);
            }
            catch (Exception exception)
            {
                AppNotificationCenter.Instance.Push(
                    "SCU",
                    L.T("Не удалось открыть ссылку: {0}", exception.Message),
                    AppNotificationKind.Warn);
            }
        }
    }
}
