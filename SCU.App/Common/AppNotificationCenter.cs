using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SCU.Common;

// Тип уведомления: определяет глиф и акцент карточки.
public enum AppNotificationKind
{
    Info,
    Success,
    Warn,
    Danger,
}

// Карточка уведомления в стиле UAC-диалогов приложения.
public sealed partial class AppNotification : ObservableObject
{
    public AppNotification(string title, string message, AppNotificationKind kind, string? actionUrl = null)
    {
        Title = title;
        Message = message;
        Kind = kind;
        ActionUrl = actionUrl;
        Timestamp = DateTime.Now;
    }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _message;

    public AppNotificationKind Kind { get; }

    // Необязательное действие: URL, открываемый системным браузером по кнопке тоста.
    public string? ActionUrl { get; }

    public bool HasAction => !string.IsNullOrEmpty(ActionUrl);

    public DateTime Timestamp { get; }

    public string KindGlyph => Kind switch
    {
        AppNotificationKind.Success => "\uE73E",   // галочка
        AppNotificationKind.Warn => "\uE7BA",      // предупреждение
        AppNotificationKind.Danger => "\uEA39",    // ошибка
        _ => "\uE946",                             // информация
    };
}

// Центр уведомлений приложения — «перехватчик» системных событий, связанных с
// SCU: завершение загрузок браузера, найденные угрозы, установка WebView2
// Runtime и т.п. Вместо системных тостов пользователь видит карточки в том же
// стиле, что и UAC-подтверждения внутри приложения (DialogBackgroundBrush,
// GlassStrokeBrush, скруглённая карточка). Отображение — NotificationHost
// в MainWindow; карточки сами исчезают через несколько секунд.
public sealed class AppNotificationCenter : ObservableObject
{
    public static AppNotificationCenter Instance { get; } = new();

    private const int MaxVisible = 5;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    public ObservableCollection<AppNotification> Items { get; } = [];

    public void Push(string title, string message, AppNotificationKind kind = AppNotificationKind.Info, string? actionUrl = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Push(title, message, kind, actionUrl));
            return;
        }

        var notification = new AppNotification(title, message, kind, actionUrl);
        Items.Insert(0, notification);
        while (Items.Count > MaxVisible)
        {
            var removed = Items[^1];
            if (_timers.Remove(removed, out var staleTimer))
            {
                staleTimer.Stop();
            }

            Items.Remove(removed);
        }

        // Автоисчезновение: живой таймер на карточку, отсчёт только на UI-потоке.
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = Lifetime,
            Tag = notification,
        };
        timer.Tick += OnTimerTick;
        timer.Start();
        _timers[notification] = timer;
    }

    // Таймеры карточек: останавливаются при ручном закрытии и вытеснении,
    // чтобы «мёртвые» тики не срабатывали по уже убранным карточкам.
    private readonly Dictionary<AppNotification, DispatcherTimer> _timers = [];

    public void Dismiss(AppNotification notification)
    {
        if (_timers.Remove(notification, out var timer))
        {
            timer.Stop();
        }

        Items.Remove(notification);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (sender is DispatcherTimer timer && timer.Tag is AppNotification notification)
        {
            timer.Stop();
            _timers.Remove(notification);
            Items.Remove(notification);
        }
    }
}
