using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using SCU.Common;
using SCU.Interop;
using SCU.Models.Browser;
using SCU.Services.Browser;
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

    private void OnSourceInitialized(object sender, EventArgs e)
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
        }

        if (e.NewValue is BrowserViewModel newVm)
        {
            newVm.ShowWindowRequested += OnShowWindowRequested;
        }
    }

    // Запуск из раздела 22 или «Показать окно» из карточки: раскрыть и вывести вперёд.
    private void OnShowWindowRequested()
    {
        if (!IsLoaded)
        {
            WindowState = WindowState.Maximized;
            Show();
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

    private void OnAccentChanged() => RefreshIcon();

    private void RefreshIcon() =>
        Icon = BrowserIconManager.CreateIcon(ThemeManager.CurrentAccentColor);

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Закрытие окна = завершение браузерной сессии: раздел 22 возвращается
        // в состояние карточки запуска, повторный «Запустить» возможен.
        ThemeManager.AccentChanged -= OnAccentChanged;
        ThemeManager.ThemeApplied -= OnThemeApplied;
        if (DataContext is BrowserViewModel vm)
        {
            vm.ShowWindowRequested -= OnShowWindowRequested;
            vm.CloseBrowser();
        }
    }

    // Геометрия глобуса для тулбара: окружность + меридиан + экватор.
    // Цвет — DynamicResource-кисть AccentFillBrush в XAML (обновляется с акцентом).
    public Geometry GlobeData
    {
        get
        {
            var geometry = new GeometryGroup
            {
                Children =
                {
                    new EllipseGeometry(new Point(11, 11), 9, 9),
                    new EllipseGeometry(new Point(11, 11), 4.05, 9),
                    new LineGeometry(new Point(2, 11), new Point(20, 11)),
                },
            };
            geometry.Freeze();
            return geometry;
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
        }
    }

    private void OnPopupToggleClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Button { Tag: string name })
        {
            return;
        }

        var wasOpen = _popupWasOpen.TryGetValue(name, out var open) && open;
        _popupWasOpen[name] = false;
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
