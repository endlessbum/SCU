using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SCU.Common;
using SCU.Interop;
using SCU.ViewModels;
using SCU.Views.Controls;

namespace SCU;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Logger _logger;
    private readonly Views.Controls.TextSelectionManager _textSelection;
    private Views.Controls.SectionHighlight? _sectionHighlight;

    // Автоматический бэнчмарк при старте: сканирование идёт в фоне после показа окна.
    public void TriggerInitialBenchmark()
    {
        TaskRunner.RunAndForget(
            _viewModel.Benchmark.RunBenchmarkCommand.ExecuteAsync(null),
            _logger,
            "initial benchmark");
    }

    // Результат TryApplyMica: при недоступном бэкдропе фон окна красится непрозрачным
    // SolidRootBrush — в том числе при каждой смене темы (OnThemeApplied).
    private bool _micaApplied;

    public MainWindow(Logger logger, SCURunner scuRunner)
    {
        InitializeComponent();
        _logger = logger;
        _viewModel = new MainViewModel(logger, scuRunner);
        DataContext = _viewModel;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;

        // Блок журнала скрывается тумблером в «Настройках»: меню и контент занимают всю высоту.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateJournalLayout(_viewModel.ShowLog);

        // Стартовая тема из настроек — до подписки, чтобы DWM-хуки не срабатывали до SourceInitialized.
        // Дальнейшие переключения в рантайме синхронизируют тёмный режим DWM с темой.
        ThemeManager.ApplyTheme(_viewModel.ThemeMode);
        ThemeManager.ThemeApplied += OnThemeApplied;

        // Значок окна (панель задач, Alt+Tab) красится цветом иконки (отдельная
        // настройка, не зависящая от акцента); при ApplyTheme выше событие ещё не
        // было подписано — стартовая покраска выполняется явно.
        ThemeManager.IconAccentChanged += OnIconAccentChanged;
        RefreshAccentIcons();

        // Подсказка поиска «Главной» открыла раздел — вокруг найденной карточки
        // пробегает пунктирный контур, чтобы настройку было легко найти глазами.
        _sectionHighlight = new Views.Controls.SectionHighlight(this, _viewModel, SidebarList, ContentScroll);
        _viewModel.SectionHighlightRequested += _sectionHighlight.OnSectionHighlightRequested;

        // Navigation host: подписка на смену раздела + начальный раздел.
        _viewModel.CurrentSectionChanged += ShowSectionView;
        _viewModel.MenuApplied += OnMenuApplied;
        ShowSectionView(_viewModel.CurrentSection?.Number);


        _textSelection = new TextSelectionManager(this);
    }

    // Подсветка раздела/карточки: Views/Controls/SectionHighlight (п. 13).

    // Mica, скругление углов и тёмный режим DWM. При недоступности бэкдропа —
    // непрозрачный фон из словаря темы вместо прозрачного окна.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowLog))
        {
            UpdateJournalLayout(_viewModel.ShowLog);
        }
        else if (e.PropertyName == nameof(MainViewModel.GlobalHotkeysEnabled))
        {
            RegisterGlobalHotkey();
        }
    }

    private HwndSource? _hwndSource;
    private bool _globalHotkeyRegistered;

    private void RegisterGlobalHotkey()
    {
        if (_hwndSource is null)
        {
            return;
        }

        if (_globalHotkeyRegistered)
        {
            Interop.GlobalHotkeys.Unregister(_hwndSource.Handle);
            _globalHotkeyRegistered = false;
        }

        if (ThemeManager.GlobalHotkeysEnabled)
        {
            _globalHotkeyRegistered = Interop.GlobalHotkeys.Register(_hwndSource.Handle);
            if (!_globalHotkeyRegistered)
            {
                _logger.Warn("HOTKEY | register failed | Ctrl+Alt+S занят другим приложением");
            }
        }
    }

    private IntPtr OnGlobalHotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Interop.GlobalHotkeys.IsHotkeyMessage(msg, unchecked((int)wParam)))
        {
            ToggleWindowVisibility();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    // Внутренние горячие клавиши: Ctrl+1..9 — быстрый переход к разделам меню.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0
            || e.Key is < Key.D1 or > Key.D9)
        {
            return;
        }

        var index = e.Key - Key.D1;
        var sections = _viewModel.Sections;
        if (index < sections.Count)
        {
            _viewModel.SelectSectionByNumber(sections[index].Number);
            e.Handled = true;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(new Interop.WindowMinMaxHandler(this).WndProc);

        // Глобальная горячая клавиша Ctrl+Alt+S: показать/скрыть SCU из любого
        // приложения. Включается тумблером в «Настройки → Горячие клавиши».
        _hwndSource = source;
        source.AddHook(OnGlobalHotkeyWndProc);
        RegisterGlobalHotkey();

        var isDark = ThemeManager.IsDarkTheme(_viewModel.ThemeMode);
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

        // Без Mica окно непрозрачное: при живой смене темы словарь подменяется,
        // но Background не обновляется через DynamicResource — перекрашиваем вручную
        // из уже подменённого словаря.
        if (!_micaApplied)
        {
            Background = (Brush)FindResource("SolidRootBrush");
        }
    }

    private void OnIconAccentChanged() => RefreshAccentIcons();

    // Значок окна в системном заголовке, на панели задач и в Alt+Tab красится
    // акцентом (Window.Icon — системная кнопка и меню сами его показывают).
    private void RefreshAccentIcons()
    {
        Icon = AccentIconManager.GetWindowIcon(ThemeManager.CurrentIconAccentColor);
    }

    // Журнал скрыт: нижняя строка сетки схлопывается, splitter убирается —
    // сайдбар и контент занимают всю высоту окна.
    private void UpdateJournalLayout(bool show)
    {
        // Стартовая высота журнала компактнее (150 px): контент раздела важнее.
        JournalRow.Height = new GridLength(show ? 150 : 0);
        JournalRow.MinHeight = show ? 120 : 0;
        JournalRow.MaxHeight = show ? 480 : 0;
        JournalSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // Размытие содержимого окна на время модального диалога (вместо тёмной подложки).
    public void SetContentBlur(bool enabled)
    {
        RootGrid.Effect = enabled ? new System.Windows.Media.Effects.BlurEffect { Radius = 12 } : null;
    }

    // Колесо всегда прокручивает страницу: разделы растянуты на всю высоту и собственной
    // прокрутки не имеют, а вложенные ScrollViewer'ы (в том числе внутренние у DataGrid)
    // иначе перехватывают колесо, даже когда прокручивать им нечего.
    private void OnContentScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift+колесо — горизонтальная прокрутка широких таблиц.
            return;
        }

        // Открытые подсказки поиска «Главной»: попап — отдельное окно (HWND), колесо
        // при фокусе в главном окне до его содержимого не доходит. Прокручиваем
        // список подсказок вместо страницы, см. DashboardView.ScrollSuggestionsBy.
        if (_viewModel.Dashboard.IsSearchSuggestionsOpen)
        {
            var dashboard = FindDescendant<Views.Sections.DashboardView>(ContentScroll);
            if (dashboard is not null)
            {
                dashboard.ScrollSuggestionsBy(e.Delta);
                e.Handled = true;
                return;
            }
        }

        ContentScroll.ScrollToVerticalOffset(ContentScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
            {
                return hit;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // Первичная загрузка данных всех вкладок. Вызывается из App до первого показа
    // окна: заставка держится на экране, пока данные не собраны.
    public async Task InitializeDataAsync()
    {
        try
        {
            await _viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Фоновое первичное чтение не должно оставлять окно в неизвестном состоянии.
            // Само открытие окна при этом остаётся возможным.
            _viewModel.LogInitializationError(exception);
        }
    }

    // WndProc/монитор/MINMAXINFO: Interop/WindowMinMaxHandler (п. 13).
    // Кнопки заголовка и перетаскивание — системные (как в окне браузера).

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _textSelection.HandleKeyDown(e);
    }

    // Выделение/копирование текста: подсистема в Views/Controls/TextSelectionManager (п. 13).

    // ===================== Navigation host (п. 13 аудита) =====================

    private readonly Dictionary<int, FrameworkElement> _sectionViews = [];

    // View раздела создаётся при первом открытии; повторное открытие — тот же
    // экземпляр (состояние VM и контролов сохраняется). Заглушка для номеров
    // без реализации живёт в XAML и управляется IsPlaceholderSection.
    private void ShowSectionView(int? number)
    {
        if (number is null)
        {
            // Разделов не осталось (все скрыты): контент очищается.
            SectionHost.Content = null;
            return;
        }

        try
        {
            SectionHost.Content = _sectionViews.TryGetValue(number.Value, out var cached)
                ? cached
                : CreateSectionView(number.Value);

            // Вкладка всегда открывается с начала страницы: смещение общего
            // ContentScroll иначе сохраняется между вкладками и переживает
            // перевёрстку после ленивой инициализации. Повторный сброс — после
            // перевёрстки (подсветка через поиск прокручивает позже и выигрывает).
            ContentScroll.ScrollToHome();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ContentScroll.ScrollToHome());
        }
        catch (Exception exception)
        {
            _logger.Error("NAV | view create failed | " + number + " | " + exception);
        }
    }

    // После правок меню: кэш view пользовательских вкладок недействителен
    // (переиспользование id показывало старое содержимое).
    private void OnMenuApplied()
    {
        var actualCustomNumbers = _viewModel.Sections
            .Where(section => section.Number >= 100)
            .Select(section => section.Number)
            .ToHashSet();
        foreach (var number in _sectionViews.Keys.Where(number => number >= 100).ToList())
        {
            if (!actualCustomNumbers.Contains(number))
            {
                _sectionViews.Remove(number);
            }
        }
    }

    private FrameworkElement CreateSectionView(int number)
    {
        var viewModel = _viewModel;
        FrameworkElement view = number switch
        {
            0 => new Views.Sections.DashboardView { DataContext = viewModel.Dashboard },
            1 => new Views.Sections.InfoView { DataContext = viewModel.Info },
            2 => new Views.Sections.ComponentsView { DataContext = viewModel.Components },
            3 => new Views.Sections.CleanupView { DataContext = viewModel.Cleanup },
            4 => new Views.Sections.BloatView { DataContext = viewModel.Bloat },
            5 => new Views.Sections.PrivacyView { DataContext = viewModel.Privacy },
            6 => new Views.Sections.ServicesView { DataContext = viewModel.Services },
            7 => new Views.Sections.StartupView { DataContext = viewModel.Startup },
            8 => new Views.Sections.PowerView { DataContext = viewModel.Power },
            9 => new Views.Sections.NetworkView { DataContext = viewModel.Network },
            10 => new Views.Sections.UIView { DataContext = viewModel.Ui },
            11 => new Views.Sections.InputView { DataContext = viewModel.Input },
            12 => new Views.Sections.MaintenanceView { DataContext = viewModel.Maintenance },
            13 => new Views.Sections.SecurityView { DataContext = viewModel.Security },
            15 => new Views.Sections.TasksView { DataContext = viewModel.Tasks },
            16 => new Views.Sections.UpdateView { DataContext = viewModel.Update },
            17 => new Views.Sections.SettingsView { DataContext = viewModel },
            18 => new Views.Sections.HistoryView { DataContext = viewModel.HistorySection },
            19 => new Views.Sections.AppsView { DataContext = viewModel.Apps },
            20 => new Views.Sections.BenchmarkView { DataContext = viewModel.Benchmark },
            21 => new Views.Sections.ScannerView { DataContext = viewModel.Scanner },
            22 => new Views.Sections.BrowserView { DataContext = viewModel.Browser },
            >= 100 => new Views.Sections.CustomUtilitiesView
            {
                DataContext = viewModel.GetCustomSectionViewModel(number),
            },
            _ => new Views.Sections.InfoView { DataContext = viewModel.Info }
        };

        _sectionViews[number] = view;
        return view;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Loaded -= OnLoaded;
        _viewModel.MenuApplied -= OnMenuApplied;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_globalHotkeyRegistered && _hwndSource is not null)
        {
            Interop.GlobalHotkeys.Unregister(_hwndSource.Handle);
        }
        ThemeManager.ThemeApplied -= OnThemeApplied;
        ThemeManager.IconAccentChanged -= OnIconAccentChanged;
        _viewModel.Dispose();
        base.OnClosing(e);
    }
}
