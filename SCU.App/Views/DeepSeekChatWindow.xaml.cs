using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using SCU.Common;
using SCU.Interop;
using SCU.Models.Browser;
using SCU.ViewModels.Sections;

namespace SCU.Views;

// Отдельное окно чата DeepSeek (раздел 24): открывается по «Открыть» в
// карточке чата. Оформление — как у окна браузера: Mica/DWM, темы, иконка.
public partial class DeepSeekChatWindow : Window
{
    private bool _micaApplied;

    private DeepSeekChatViewModel? ViewModel => DataContext as DeepSeekChatViewModel;

    private DeepSeekChatSessionItem? _subscribedSession;

    public DeepSeekChatWindow()
    {
        InitializeComponent();
        DataContextChanged += OnWindowDataContextChanged;
        ThemeManager.AccentChanged += OnAccentChanged;
        ThemeManager.ThemeApplied += OnThemeApplied;
        SourceInitialized += OnSourceInitialized;
        RefreshIcon();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var isDark = ThemeManager.IsDarkTheme(ThemeManager.LoadThemeMode());
        _micaApplied = WindowEffects.TryApplyMica(this, isDark);
        if (!_micaApplied)
        {
            Background = (System.Windows.Media.Brush)FindResource("SolidRootBrush");
        }

        // Строка заголовка — цветом фона приложения, чтобы не выделялась.
        WindowEffects.ApplyCaptionColor(this, isDark);
    }

    private void OnThemeApplied(bool isDark)
    {
        WindowEffects.ApplyCaptionColor(this, isDark);
        if (!_micaApplied)
        {
            // Без Mica DynamicResource не обновляет Background при смене темы.
            Background = (System.Windows.Media.Brush)FindResource("SolidRootBrush");
        }
    }

    private void OnAccentChanged() => RefreshIcon();

    private void RefreshIcon() =>
        Icon = BrowserIconManager.CreateIcon(ThemeManager.CurrentAccentColor);

    private void OnWindowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DeepSeekChatViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is DeepSeekChatViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        }

        SubscribeActiveSession(ViewModel?.ActiveSession);
    }

    private void SubscribeActiveSession(DeepSeekChatSessionItem? session)
    {
        if (ReferenceEquals(_subscribedSession, session))
        {
            return;
        }

        if (_subscribedSession is { } oldSession)
        {
            oldSession.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        }

        _subscribedSession = session;
        if (session is { } newSession)
        {
            newSession.Messages.CollectionChanged += OnMessagesCollectionChanged;
        }
    }

    // Смена активного чата: переписка перечитывается, автопрокрутка переподписывается.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeepSeekChatViewModel.ActiveSession) && ViewModel is { } vm)
        {
            SubscribeActiveSession(vm.ActiveSession);
            MessagesScroll.ScrollToEnd();
        }

        if (e.PropertyName == nameof(DeepSeekChatViewModel.IsWaiting) && ViewModel is { IsWaiting: true })
        {
            MessagesScroll.ScrollToEnd();
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            MessagesScroll.ScrollToEnd();
        }
    }

    // Enter — отправить, Shift+Enter — перенос строки (AcceptsReturn=True).
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        if (ViewModel?.SendCommand.CanExecute(null) == true)
        {
            ViewModel.SendCommand.Execute(null);
        }

        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.AccentChanged -= OnAccentChanged;
        ThemeManager.ThemeApplied -= OnThemeApplied;
        if (DataContext is DeepSeekChatViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnClosed(e);
    }
}
