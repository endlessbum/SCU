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
        StateChanged += (_, _) => UpdateMaximizedState();
        // Окно стартует развёрнутым (WindowState=Maximized в XAML): событие StateChanged
        // при этом может не сработать — выставляем глиф кнопки сразу.
        UpdateMaximizedState();

        // Блок журнала скрывается тумблером в «Настройках»: меню и контент занимают всю высоту.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowLog))
            {
                UpdateJournalLayout(_viewModel.ShowLog);
            }
        };
        UpdateJournalLayout(_viewModel.ShowLog);

        // Стартовая тема из настроек — до подписки, чтобы DWM-хуки не срабатывали до SourceInitialized.
        // Дальнейшие переключения в рантайме синхронизируют тёмный режим DWM с темой.
        ThemeManager.ApplyTheme(_viewModel.ThemeMode);
        ThemeManager.ThemeApplied += OnThemeApplied;

        // Знак в заголовке и значок окна красятся акцентом; при ApplyTheme выше
        // AccentChanged ещё не был подписан, поэтому стартовая покраска — явно.
        ThemeManager.AccentChanged += OnAccentChanged;
        RefreshAccentIcons();

        // Подсказка поиска «Главной» открыла раздел — вокруг найденной карточки
        // пробегает пунктирный контур, чтобы настройку было легко найти глазами.
        _sectionHighlight = new Views.Controls.SectionHighlight(this, _viewModel, SidebarList, ContentScroll);
        _viewModel.SectionHighlightRequested += _sectionHighlight.OnSectionHighlightRequested;

        // Navigation host: подписка на смену раздела + начальный раздел.
        _viewModel.CurrentSectionChanged += ShowSectionView;
        ShowSectionView(_viewModel.CurrentSection?.Number);

        _textSelection = new TextSelectionManager(this);
    }

    // Подсветка раздела/карточки: Views/Controls/SectionHighlight (п. 13).

    // Mica, скругление углов и тёмный режим DWM. При недоступности бэкдропа —
    // непрозрачный фон из словаря темы вместо прозрачного окна.
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(new Interop.WindowMinMaxHandler(this).WndProc);

        var isDark = ThemeManager.IsDarkTheme(_viewModel.ThemeMode);
        _micaApplied = WindowEffects.TryApplyMica(this, isDark);
        if (!_micaApplied)
        {
            Background = (Brush)FindResource("SolidRootBrush");
        }
    }

    private void OnThemeApplied(bool isDark)
    {
        WindowEffects.UpdateDarkMode(this, isDark);

        // Без Mica окно непрозрачное: при живой смене темы словарь подменяется,
        // но Background не обновляется через DynamicResource — перекрашиваем вручную
        // из уже подменённого словаря.
        if (!_micaApplied)
        {
            Background = (Brush)FindResource("SolidRootBrush");
        }
    }

    private void OnAccentChanged() => RefreshAccentIcons();

    // Иконка и знак SCU в заголовке, значок окна (панель задач, Alt+Tab) следуют
    // за акцентным цветом из настроек: цвет тот же, что у AccentFillBrush.
    private void RefreshAccentIcons()
    {
        TitleIcon.Source = AccentIconManager.GetTitleBarIcon();
        Icon = AccentIconManager.GetWindowIcon();
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

    private void UpdateMaximizedState()
    {
        // Скругление и тень от DWM; при maximize система сама убирает скругление.
        // Меняется только глиф кнопки: развернуть (E922) / восстановить (E923).
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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
            return;
        }

        try
        {
            SectionHost.Content = _sectionViews.TryGetValue(number.Value, out var cached)
                ? cached
                : CreateSectionView(number.Value);
        }
        catch (Exception exception)
        {
            _logger.Error("NAV | view create failed | " + number + " | " + exception);
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
            _ => new Views.Sections.InfoView { DataContext = viewModel.Info }
        };

        _sectionViews[number] = view;
        return view;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Loaded -= OnLoaded;
        ThemeManager.ThemeApplied -= OnThemeApplied;
        ThemeManager.AccentChanged -= OnAccentChanged;
        _viewModel.Dispose();
        base.OnClosing(e);
    }
}
