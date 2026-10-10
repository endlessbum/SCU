using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using SCU.Common;
using SCU.Interop;
using SCU.Models.Browser;
using SCU.ViewModels.Sections;

namespace SCU.Views;

// Отдельное окно встроенного браузера (раздел 22): открывается по «Запустить»,
// развёрнуто на весь экран, минимум размера — как у основного окна SCU.
// Реализует IBrowserHostBridge: создание/подмена/закрытие WebView2-контролов.
public partial class BrowserWindow : Window, IBrowserHostBridge
{
    private readonly Dictionary<int, WebView2> _tabViews = [];

    // Popup'ы закрываются по mousedown вне их (StaysOpen=False) и через TwoWay
    // пишут false в VM раньше Click кнопки-тумблера — без этого флага повторный
    // клик по кнопке переоткрывал панель вместо закрытия.
    private readonly Dictionary<string, bool> _popupWasOpen = new();

    private BrowserViewModel? ViewModel => DataContext as BrowserViewModel;

    public BrowserWindow()
    {
        InitializeComponent();
        DataContextChanged += OnWindowDataContextChanged;
        Closing += OnWindowClosing;
        ThemeManager.AccentChanged += OnAccentChanged;
        // Как в главном окне SCU: DWM-подложка (Mica) и тёмный режим DWM —
        // системный заголовок сливается с фоном приложения. При недоступном
        // бэкдропе окно красится SolidRootBrush из словаря темы.
        ThemeManager.ThemeApplied += OnThemeApplied;
        SourceInitialized += OnSourceInitialized;
        RefreshIcon();
    }

    private bool _micaApplied;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var isDark = ThemeManager.IsDarkTheme(ThemeManager.LoadThemeMode());
        _micaApplied = WindowEffects.TryApplyMica(this, isDark);
        if (!_micaApplied)
        {
            Background = (Brush)FindResource("SolidRootBrush");
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
            Background = (Brush)FindResource("SolidRootBrush");
        }
    }

    // ===================== Окно: показ, иконка, закрытие =====================

    private void OnWindowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is BrowserViewModel oldVm)
        {
            oldVm.ShowWindowRequested -= OnShowWindowRequested;
            oldVm.Tabs.CollectionChanged -= OnTabsCollectionChanged;
        }

        if (e.NewValue is BrowserViewModel newVm)
        {
            newVm.ShowWindowRequested += OnShowWindowRequested;
            newVm.Tabs.CollectionChanged += OnTabsCollectionChanged;
        }
    }

    // Запуск из раздела 22 или «Показать окно» из карточки: раскрыть и вывести вперёд.
    private void OnShowWindowRequested()
    {
        if (!IsLoaded)
        {
            WindowState = WindowState.Maximized;
            Show();
            UiAnimations.AppearWindow(this);
        }
        else
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Maximized;
            }

            Activate();
        }
    }

    // ===================== Анимации вкладок =====================

    // Новая шапка вкладки «вырастает» из левого края. Контейнер реализуется
    // лениво — анимация после первого layout-прохода (priority Loaded).
    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        foreach (BrowserTabModel tab in e.NewItems)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AnimateTabHeaderIn(tab));
        }
    }

    private void AnimateTabHeaderIn(BrowserTabModel tab)
    {
        if (!IsVisible || TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement container)
        {
            return;
        }

        UiAnimations.ScaleFadeIn(container);
    }

    // Мост перед фактическим закрытием: шапка плавно гаснет, VM удаляет вкладку
    // после завершения анимации (таймаут — страховка от «вечной» вкладки,
    // если окно ушло с экрана посреди анимации).
    public async Task<bool> AnimateTabCloseAsync(BrowserTabModel tab)
    {
        if (!IsVisible || !UiAnimations.Enabled
            || TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement container)
        {
            return true;
        }

        var finished = new TaskCompletionSource();
        UiAnimations.FadeOutThen(container, UiAnimations.TabMs, () => finished.TrySetResult());
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromMilliseconds(UiAnimations.TabMs + 400)).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
        }

        return true;
    }

    private void OnAccentChanged() => RefreshIcon();

    private void RefreshIcon() =>
        Icon = BrowserIconManager.CreateIcon(ThemeManager.CurrentAccentColor);

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            // Второй проход после асинхронной очистки: сессия уже закрыта.
            DetachFromViewModel();
            return;
        }

        if (_closeInProgress)
        {
            // Закрытие уже идёт (очистка данных сайтов) — только гасим повторный X.
            e.Cancel = true;
            return;
        }

        if (DataContext is not BrowserViewModel vm)
        {
            return;
        }

        if (vm.NeedsExitCleanup)
        {
            // Двухфазное закрытие (аудит lifecycle): очистка данных сайтов
            // выполняется через await — без блокировки диспетчера. Окно
            // закрывается повторно по завершении очистки.
            e.Cancel = true;
            _closeInProgress = true;
            _ = CompleteCloseAsync(vm);
            return;
        }

        vm.CloseBrowser();
        _closeConfirmed = true;
        DetachFromViewModel();
    }

    private bool _closeConfirmed;
    private bool _closeInProgress;

    private async Task CompleteCloseAsync(BrowserViewModel vm)
    {
        try
        {
            await vm.CloseBrowserAsync();
        }
        finally
        {
            _closeInProgress = false;
            _closeConfirmed = true;
            Close();
        }
    }

    private void DetachFromViewModel()
    {
        ThemeManager.AccentChanged -= OnAccentChanged;
        ThemeManager.ThemeApplied -= OnThemeApplied;
        if (DataContext is BrowserViewModel vm)
        {
            vm.ShowWindowRequested -= OnShowWindowRequested;
            vm.Tabs.CollectionChanged -= OnTabsCollectionChanged;
            // vm.CloseBrowser() не вызывается: сессия уже закрыта
            // (синхронно в одноразовом пути либо в CloseBrowserAsync).
        }
    }

    // ===================== IBrowserHostBridge =====================

    public async Task<bool> InitializeAsync(BrowserViewModel viewModel, BrowserTabModel firstTab)
    {
        // Окно обязано быть показано ДО создания контроллера WebView2: HwndHost
        // получает HWND только у показанного окна — иначе EnsureCoreWebView2Async
        // ждёт вечно (Show был только после успешной инициализации).
        OnShowWindowRequested();

        await CreateTabViewAsync(firstTab).ConfigureAwait(true);
        ActivateTabView(firstTab);
        return true;
    }

    public async Task CreateTabViewAsync(BrowserTabModel tab)
    {
        var vm = ViewModel
            ?? throw new InvalidOperationException("Browser data context is not set.");

        // Поколение сессии на входе: закрытие окна во время создания вкладки
        // не должно оставлять живой WebView2 вне сессии (H-03).
        var session = vm.SessionGeneration;

        var host = new BrowserTabHost(tab, vm.Service, vm, vm.Settings, vm.Log);
        var view = host.CreateView();

        // Контрол обязан попасть в дерево видимым ДО EnsureCoreWebView2Async:
        // HwndHost без HWND не создаёт CoreWebView2Controller, и задача висит вечно.
        view.Visibility = Visibility.Visible;
        WebViewPanel.Children.Add(view);
        _tabViews[tab.Id] = view;

        try
        {
            await host.InitializeAsync(tab.Url).ConfigureAwait(true);
        }
        catch
        {
            WebViewPanel.Children.Remove(view);
            _tabViews.Remove(tab.Id);
            host.Dispose();
            throw;
        }

        // Вкладку могли закрыть, пока шло создание — хост не регистрируем.
        if (!_tabViews.TryGetValue(tab.Id, out var registered) || !ReferenceEquals(registered, view))
        {
            host.Dispose();
            return;
        }

        // Окно закрыли во время создания — хост не регистрируем и освобождаем.
        if (vm.SessionGeneration != session)
        {
            WebViewPanel.Children.Remove(view);
            _tabViews.Remove(tab.Id);
            host.Dispose();
            return;
        }

        vm.RegisterHost(tab, host);
    }

    public void ActivateTabView(BrowserTabModel tab)
    {
        foreach (var pair in _tabViews)
        {
            pair.Value.Visibility = pair.Key == tab.Id ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void CloseTabView(BrowserTabModel tab)
    {
        if (_tabViews.Remove(tab.Id, out var view))
        {
            WebViewPanel.Children.Remove(view);
        }
    }

    public void FocusAddress() => Dispatcher.Invoke(() =>
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    });

    // ===================== Адресная строка =====================

    private void OnAddressGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ViewModel?.IsEditingAddress = true;

    private void OnAddressLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.IsEditingAddress = false;
            // После ухода из строки — синхронизация с фактическим адресом вкладки.
            vm.OnActiveTabUrlChanged();
        }
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                vm.NavigateAddressCommand.Execute(null);
                FocusWebView();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.AddressEscapeCommand.Execute(null);
                FocusWebView();
                e.Handled = true;
                break;
        }
    }

    private void FocusWebView()
    {
        if (ViewModel?.ActiveTab is { } tab && _tabViews.TryGetValue(tab.Id, out var view))
        {
            view.Focus();
        }
    }

    // ===================== Горячие клавиши =====================

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Обработка делегирована в VM: тот же код обслуживает KeyDown вкладки,
        // когда фокус ушёл в web-контент (HwndHost).
        ViewModel?.HandleShortcut(e.Key, Keyboard.Modifiers, e);
    }

    // ===================== Тумблеры popup-панелей =====================

    private void OnPopupTogglePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            // Состояние ДО закрытия popup'а по mousedown вне его.
            _popupWasOpen[name] = FindPopup(name)?.IsOpen == true;
            _popupMouseDownSeen = true;
        }
    }

    // PreviewMouseDown предшествует только мышиному клику: клавиатурная
    // активация (Space/Enter) даёт Click без него (аудит a11y).
    private bool _popupMouseDownSeen;

    // Клавиатурная активация шапки вкладки (аудит a11y): Enter/Space = клик.
    private void OnTabHeaderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || sender is not FrameworkElement { DataContext: BrowserTabModel tab })
        {
            return;
        }

        ViewModel?.ActivateTabCommand.Execute(tab);
        e.Handled = true;
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Отпускание кнопки вне тумблера (Click не сработал): сбрасываем флаг,
        // чтобы последующая клавиатурная активация не взяла устаревшее значение.
        _popupMouseDownSeen = false;
    }

    private void OnPopupToggleClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Button { Tag: string name })
        {
            return;
        }

        var seen = _popupMouseDownSeen;
        _popupMouseDownSeen = false;
        var wasOpen = seen && _popupWasOpen.TryGetValue(name, out var open) && open;
        if (!seen)
        {
            // Клавиатурная активация: фактическое состояние popup'а вместо
            // устаревшего значения прошлого мышиного клика.
            wasOpen = FindPopup(name)?.IsOpen == true;
        }
        else
        {
            _popupWasOpen[name] = false;
        }

        Action<bool> apply = name switch
        {
            "Downloads" => value => vm.IsDownloadsOpen = value,
            "History" => value => vm.IsHistoryOpen = value,
            "Bookmarks" => value => vm.IsBookmarksOpen = value,
            "Settings" => value => vm.IsSettingsOpen = value,
            _ => _ => { },
        };

        apply(!wasOpen);
    }

    private Popup? FindPopup(string name) => name switch
    {
        "Downloads" => DownloadsPopup,
        "History" => HistoryPopup,
        "Bookmarks" => BookmarksPopup,
        "Settings" => SettingsPopup,
        _ => null,
    };

    // ===================== Настройки: папка загрузок =====================

    private void OnPickDownloadFolderClick(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }

        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = L.T("Выбор папки загрузок"),
                InitialDirectory = vm.Settings.DownloadFolder,
            };
            if (dialog.ShowDialog() == true)
            {
                vm.SaveDownloadFolderCommand.Execute(dialog.FolderName);
            }
        }
        catch (Exception exception)
        {
            vm.Log.Warn("BROWSER | pick download folder failed | " + exception.Message);
        }
    }
}
