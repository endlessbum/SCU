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

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Поведение при закрытии — явный выбор пользователя (дизайн, п. 2):
        // «Закрывать SCU» или «Сворачивать в трей».
        if (App.RunInBackgroundOnClose)
        {
            // Завершение сеанса Windows (выход/выключение) не блокируем:
            // отмена закрытия заставляла ОС показывать «приложение
            // препятствует завершению работы».
            if (App.IsSessionEnding)
            {
                return;
            }

            // Окно скрывается полностью: исчезает из панели задач и Alt+Tab,
            // остаются иконка трея и глобальная горячая клавиша.
            e.Cancel = true;
            Hide();
        }
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
        Closing += OnWindowClosing;

        // Блок журнала скрывается тумблером в «Настройках»: меню и контент занимают всю высоту.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateJournalLayout(_viewModel.ShowLog);

        // Стартовая тема из настроек — до подписки, чтобы DWM-хуки не срабатывали до SourceInitialized.
        // Дальнейшие переключения в рантайме синхронизируют тёмный режим DWM с темой.
        ThemeManager.ApplyTheme(_viewModel.ThemeMode);
        ThemeManager.ThemeApplied += OnThemeApplied;

        // Фоновый режим (сворачивание в трей) — по выбору пользователя
        // в настройках (дизайн, п. 2).
        App.RunInBackgroundOnClose = ThemeManager.CloseBehavior == AppCloseBehavior.MinimizeToTray;

        // Значок окна (панель задач, Alt+Tab) красится цветом иконки (отдельная
        // настройка, не зависящая от акцента); при ApplyTheme выше событие ещё не
        // было подписано — стартовая покраска выполняется явно.
        ThemeManager.IconAccentChanged += OnIconAccentChanged;
        RefreshAccentIcons();
        UpdateDesktopShortcutIcons();

        // Подсказка поиска «Главной» открыла раздел — вокруг найденной карточки
        // пробегает пунктирный контур, чтобы настройку было легко найти глазами.
        _sectionHighlight = new Views.Controls.SectionHighlight(
            this, _viewModel, SidebarList, ContentScroll, UserScriptsStripHost);
        _viewModel.SectionHighlightRequested += _sectionHighlight.OnSectionHighlightRequested;

        // Navigation host: подписка на смену раздела + начальный раздел.
        _viewModel.CurrentSectionChanged += ShowSectionView;
        _viewModel.CurrentSectionChanged += OnCurrentSectionChangedForNavPill;
        _viewModel.MenuApplied += OnMenuApplied;
        ShowSectionView(_viewModel.CurrentSection?.Number);
        QueueNavPillUpdate(animate: false);

        // Пилюля сайдбара всегда следует за CurrentSection — тем же источником
        // истины, который переключает контент. Нельзя опираться на
        // SidebarList.SelectedItem: SelectionChanged и ItemContainerGenerator.StatusChanged
        // могут приходить в промежуточном состоянии (особенно при TwoWay binding и
        // перестроении сгруппированного списка), из-за чего индикатор «отстаёт» на
        // предыдущем пункте. StatusChanged и ScrollChanged здесь только повторно
        // пересчитывают геометрию уже активного пункта.
        SidebarList.ItemContainerGenerator.StatusChanged += (_, _) => QueueNavPillUpdate(animate: false);
        SidebarList.Loaded += (_, _) =>
        {
            if (FindDescendant<ScrollViewer>(SidebarList) is { } scroll)
            {
                scroll.ScrollChanged += (_, _) => QueueNavPillUpdate(animate: false);
            }
        };


        _textSelection = new TextSelectionManager(this);

        // Ресурсы освобождаются только при фактическом закрытии (см. OnClosing/OnClosed).
        _shutdown = new WindowShutdown(ReleaseResources);
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
        UiAnimations.FadeIn(this, 160);
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
        // Единое правило заголовка (п. 8): разворачивание строго в рабочую область —
        // строка заголовка одной высоты во всех состояниях окна.
        Interop.WorkAreaMaximize.Attach(this);

        var source = (HwndSource)PresentationSource.FromVisual(this)!;

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

        // Иконка в области уведомлений: штатный путь завершить SCU, когда закрытие
        // окна уходит в фоновый режим (сворачивание в трей). Ставится на тот же
        // HWND, что и глобальная горячая клавиша — иконка живёт, пока живёт окно.
        // Контекстное меню (правая кнопка) строит MainWindow — WPF, стиль приложения.
        _trayIcon = new Interop.TrayIcon(
            source,
            _logger,
            new Interop.TrayIconHost(
                ShowFromTray,
                ExitFromTray,
                ShowTrayContextMenu));
        _trayIcon.Show();
    }

    private Interop.TrayIcon? _trayIcon;

    // Открытое сейчас меню трея: нужно, чтобы повторный правый клик по иконке
    // закрывал предыдущее меню синхронно, до построения нового.
    private System.Windows.Controls.ContextMenu? _openTrayMenu;

    // «Показать SCU»: окно поднимается из фонового режима (свёрнутое — разворачивается).
    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // Окно знакомства откладывается до первого подъёма окна: при старте
        // с --tray оно не должно выскочить на логоне. ShowOnce сам no-op,
        // если знакомство уже показано.
        Dispatcher.BeginInvoke(() => Views.OnboardingWindow.ShowOnce(this));
    }

    // «Выход» в трее. Фоновый режим снимается явно: иначе повторное закрытие
    // окна снова отменилось бы, и приложение осталось бы висеть в процессе.
    private void ExitFromTray()
    {
        _logger.Info("TRAY | exit requested");
        App.RunInBackgroundOnClose = false;
        Close();
    }

    // ===================== Контекстное меню трея =====================

    // Номера разделов, открываемых из меню: «Сканер», «Автозагрузка», «Настройки».
    private const int ScannerSectionNumber = 21;
    private const int StartupSectionNumber = 7;
    private const int SettingsSectionNumber = 17;

    // Меню правой кнопки по иконке трея: WPF ContextMenu в стиле приложения.
    // Трей обязан работать и когда главное окно скрыто, а попапу нужен живой
    // PlacementTarget — меню показывается через нулевое прозрачное окно-носитель,
    // которое закрывается вместе с меню.
    private void ShowTrayContextMenu()
    {
        // Повторный правый клик по иконке: предыдущее меню закрывается
        // синхронно (его Closed-обработчик снимет хук и закроет окно-носитель),
        // иначе на мгновение висели бы два меню.
        _openTrayMenu?.IsOpen = false;

        var host = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Content = null,
        };

        var menu = BuildTrayContextMenu();
        menu.PlacementTarget = host;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

        // Скрытие по клику мимо меню. StaysOpen=false закрывает меню только по
        // нажатиям внутри приложения; когда главное окно скрыто в трее, клики
        // по рабочему столу и чужим окнам до WPF не доходят — на время жизни
        // меню ставится низкоуровневый хук мыши, закрывающий его извне попапа.
        OutsideMouseHook? outsideHook = null;
        menu.Opened += (_, _) =>
        {
            // Если HWND попапа не достался — хук не ставится, остаётся
            // штатное поведение StaysOpen=false: деградация, не падение.
            if (PresentationSource.FromVisual(menu) is HwndSource popup)
            {
                outsideHook = new OutsideMouseHook(popup.Handle);
                if (!outsideHook.Installed)
                {
                    _logger.Warn("TRAY | outside mouse hook not installed");
                }

                outsideHook.OutsideMouseDown += () =>
                    Dispatcher.BeginInvoke(() => menu.IsOpen = false);
            }
        };
        menu.Closed += (_, _) =>
        {
            outsideHook?.Dispose();
            if (_openTrayMenu == menu)
            {
                _openTrayMenu = null;
            }

            host.Close();
        };

        _openTrayMenu = menu;
        try
        {
            host.Show();
            menu.IsOpen = true;
        }
        catch
        {
            // Попап не открылся — Closed не поднимется: снимаем хук и убираем
            // окно-носитель сами, иначе они копятся с каждым сбоем. Ошибку
            // перебрасываем: SafeInvoke в TrayIcon залогирует её.
            outsideHook?.Dispose();
            if (_openTrayMenu == menu)
            {
                _openTrayMenu = null;
            }

            host.Close();
            throw;
        }
    }

    private ContextMenu BuildTrayContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("TrayContextMenu"),
            StaysOpen = false,
        };

        foreach (var entry in Interop.TrayMenu.Build(_viewModel.Cleanup.IsBusy))
        {
            var item = new MenuItem
            {
                Style = (Style)FindResource("TrayMenuItem"),
                IsEnabled = entry.Enabled,
            };

            switch (entry.Command)
            {
                case Interop.TrayMenuCommand.Clean:
                    // «Очистить всё» из раздела «Очистка системы и диск» — с его
                    // подтверждением; DataContext у процентов — CleanupViewModel.
                    item.DataContext = _viewModel.Cleanup;
                    item.Header = BuildTrayCleanHeader(entry.Title);
                    item.SetBinding(
                        System.Windows.Controls.MenuItem.CommandProperty,
                        new System.Windows.Data.Binding(nameof(ViewModels.Sections.CleanupViewModel.CleanAllCommand)));
                    break;
                case Interop.TrayMenuCommand.Scanner:
                    item.Header = entry.Title;
                    item.Click += (_, _) => OpenSectionFromTray(ScannerSectionNumber);
                    break;
                case Interop.TrayMenuCommand.Startup:
                    item.Header = entry.Title;
                    item.Click += (_, _) => OpenSectionFromTray(StartupSectionNumber);
                    break;
                case Interop.TrayMenuCommand.Settings:
                    item.Header = entry.Title;
                    item.Click += (_, _) => OpenSectionFromTray(SettingsSectionNumber);
                    break;
                case Interop.TrayMenuCommand.Exit:
                    item.Header = entry.Title;
                    item.Click += (_, _) => ExitFromTray();
                    break;
            }

            menu.Items.Add(item);
        }

        return menu;
    }

    // Заголовок пункта «Очистить»: название слева, проценты справа. Проценты
    // привязаны к CleanupViewModel и скрываются, когда очистка не идёт.
    private static System.Windows.Controls.Grid BuildTrayCleanHeader(string title)
    {
        var header = new System.Windows.Controls.Grid();
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock { Text = title, FontSize = 12 };
        System.Windows.Controls.Grid.SetColumn(titleText, 0);
        header.Children.Add(titleText);

        var percent = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        percent.SetBinding(
            TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(ViewModels.Sections.CleanupViewModel.CleanupProgressText)));
        var percentStyle = new Style(typeof(TextBlock));
        var emptyTrigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(ViewModels.Sections.CleanupViewModel.CleanupProgressText)),
            Value = string.Empty,
        };
        emptyTrigger.Setters.Add(new Setter(TextBlock.VisibilityProperty, Visibility.Collapsed));
        percentStyle.Triggers.Add(emptyTrigger);
        percent.Style = percentStyle;
        System.Windows.Controls.Grid.SetColumn(percent, 1);
        header.Children.Add(percent);

        return header;
    }

    // Пункты навигации: окно поднимается из фонового режима и открывается
    // на выбранном разделе — тот же механизм, что у клика по сайдбару.
    private void OpenSectionFromTray(int sectionNumber)
    {
        ShowFromTray();
        _viewModel.SelectSectionByNumber(sectionNumber);
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

    private void OnIconAccentChanged()
    {
        RefreshAccentIcons();
        UpdateDesktopShortcutIcons();
        // Иконка трея — из того же набора окрашенных .ico, что и ярлыки.
        _trayIcon?.RefreshIcon();
    }

    // Значок окна в системном заголовке, на панели задач и в Alt+Tab красится
    // акцентом (Window.Icon — системная кнопка и меню сами его показывают).
    private void RefreshAccentIcons()
    {
        Icon = AccentIconManager.GetWindowIcon(ThemeManager.CurrentIconAccentColor);
    }

    // Ярлыки в системе (рабочий стол, меню «Пуск») перепривязываются на окрашенную
    // иконку выбранного пресета: иконку exe в рантайме менять нельзя. COM-доступ
    // к .lnk и уведомление оболочки идут в фоне. Стартовый вызов переносит ярлык
    // инсталлятора (иконка exe) на цвет из настроек. От темы иконка не зависит —
    // при смене темы ярлыки не трогаются.
    private void UpdateDesktopShortcutIcons() =>
        TaskRunner.RunAndForget(
            Task.Run(() => DesktopIconService.Apply(ThemeManager.IconAccent, _logger)),
            _logger,
            "desktop icon refresh");

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
        // Окно знакомства при первом запуске: после отрисовки главного окна
        // (BeginInvoke), чтобы размывался уже видимый интерфейс. При старте
        // с --tray не показываем — знакомство отложено до ShowFromTray.
        if (!App.StartInTray)
        {
            Dispatcher.BeginInvoke(() => Views.OnboardingWindow.ShowOnce(this));
        }
    }

    // Первичная загрузка данных всех вкладок. Вызывается из App до первого показа
    // окна: заставка с индикатором держится на экране, пока данные не собраны.
    // progress — доля завершённых разделов (0..100) для индикатора на заставке.
    public async Task InitializeDataAsync(IProgress<double>? progress = null)
    {
        try
        {
            await _viewModel.InitializeAsync(progress).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Фоновое первичное чтение не должно оставлять окно в неизвестном состоянии.
            // Само открытие окна при этом остаётся возможным.
            _viewModel.LogInitializationError(exception);
        }
    }

    // Разворачивание в рабочую область монитора: Interop/WorkAreaMaximize (п. 13 и п. 8).
    // Кнопки заголовка и перетаскивание — системные (как в окне браузера).

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _textSelection.HandleKeyDown(e);
    }

    // Выделение/копирование текста: подсистема в Views/Controls/TextSelectionManager (п. 13).

    // ===================== Горячие клавиши =====================

    // Ctrl+Shift+A — окно SCU AI Assistant из любого раздела; F5 — обновление
    // данных текущего раздела. F5 работает только там, где данные могут устареть
    // после правок и есть кнопка «Обновить» (Автозагрузка 7, Очистка 3,
    // Кэш приложений 26); на остальных разделах — no-op.
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            RefreshCurrentSectionByHotkey();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenAiAssistantByHotkey();
            e.Handled = true;
        }
    }

    private void RefreshCurrentSectionByHotkey()
    {
        var command = _viewModel.CurrentSection?.Number switch
        {
            7 => _viewModel.Startup.RefreshCommand,
            3 => _viewModel.Cleanup.RefreshCommand,
            26 => _viewModel.AppCache.RefreshCommand,
            27 => _viewModel.Defender.RefreshCommand,
            _ => null,
        };

        // CanExecute учитывает busy-состояния раздела: во время скана/операции
        // F5 повторный запуск не инициирует.
        if (command is { } refresh && refresh.CanExecute(null))
        {
            refresh.Execute(null);
        }
    }

    private void OpenAiAssistantByHotkey()
    {
        // Раздел 24 «DeepSeek»: если view ещё не создавалась — создаём и
        // кэшируем без переключения текущего раздела; создание подписывает
        // её события на VM, окно ассистента открывается как по кнопке.
        if (!_sectionViews.TryGetValue(24, out var cached))
        {
            cached = CreateSectionView(24);
        }

        (cached as Views.Sections.DeepSeekView)?.OpenAssistantWindow();
    }

    // ===================== Navigation host (п. 13 аудита) =====================

    private readonly Dictionary<int, FrameworkElement> _sectionViews = [];

    // View раздела создаётся при первом открытии; повторное открытие — тот же
    // экземпляр (состояние VM и контролов сохраняется). Заглушка для номеров
    // без реализации живёт в XAML и управляется IsPlaceholderSection.
    private void ShowSectionView(int? number)
    {
        if (number is null)
        {
            // Разделов не осталось (все скрыты): контент очищается. Пилюлю прячет
            // обработчик CurrentSectionChanged при сбросе активного раздела.
            SectionHost.Content = null;
            _lastShownSectionNumber = null;
            return;
        }

        try
        {
            var isFirstShow = _lastShownSectionNumber is null;
            var isCached = _sectionViews.TryGetValue(number.Value, out var cached);
            var content = isCached ? cached! : CreateSectionView(number.Value);
            SectionHost.Content = content;

            // Переход между разделами: повторное открытие — выезд сбоку по
            // направлению списка (вперёд — слева направо, назад — наоборот);
            // первое открытие — каскадное появление карточек. Первый показ
            // раздела при старте приложения — без анимации.
            if (!isFirstShow && number.Value != _lastShownSectionNumber)
            {
                if (isCached)
                {
                    var fromX = 0d;
                    if (_lastShownSectionNumber is { } previous)
                    {
                        var (oldIndex, newIndex) = (IndexOfSection(previous), IndexOfSection(number.Value));
                        if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
                        {
                            fromX = newIndex > oldIndex ? 16 : -16;
                        }
                    }

                    UiAnimations.SlideFadeIn(content, fromX, fromY: 0);
                }
                else
                {
                    StaggerSectionView(content);
                }

                UiAnimations.FadeIn(SectionHeaderPanel, UiAnimations.SectionMs);
            }
            _lastShownSectionNumber = number;

            // Вкладка всегда открывается с начала страницы: смещение общего
            // ContentScroll иначе сохраняется между вкладками и переживает
            // перевёрстку после ленивой инициализации. Повторный сброс — после
            // перевёрстки (подсветка через поиск прокручивает позже и выигрывает).
            ContentScroll.ScrollToHome();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ContentScroll.ScrollToHome());

            // Пилюлю здесь не двигаем: обработчик CurrentSectionChanged запускает
            // отдельное обновление после текущего цикла layout и использует сам
            // CurrentSection, а не промежуточный SidebarList.SelectedItem.
        }
        catch (Exception exception)
        {
            _logger.Error("NAV | view create failed | " + number + " | " + exception);
        }
    }

    private int? _lastShownSectionNumber;

    // Каскад при первом открытии раздела: раскрываем корневую панель view.
    // Корень не обязан быть панелью (часто Border/ScrollViewer) — раскрываем до 3 слоёв.
    private static void StaggerSectionView(FrameworkElement view)
    {
        var content = view is ContentControl control ? control.Content : null;
        if (FindStaggerRoot(content) is { } panel && panel.Children.Count > 0)
        {
            UiAnimations.StaggerIn(panel);
        }
        else
        {
            UiAnimations.SlideFadeIn(view);
        }
    }

    private static Panel? FindStaggerRoot(object? content)
    {
        for (var depth = 0; depth < 3 && content is not null; depth++)
        {
            if (content is Panel panel)
            {
                return panel;
            }

            content = content switch
            {
                Border border => border.Child,
                ScrollViewer scroll => scroll.Content,
                _ => null
            };
        }

        return null;
    }

    private int IndexOfSection(int number)
    {
        for (var i = 0; i < _viewModel.Sections.Count; i++)
        {
            if (_viewModel.Sections[i].Number == number)
            {
                return i;
            }
        }

        return -1;
    }

    // ===================== Едущая пилюля сайдбара =====================

    // Последнее запланированное обновление. При серии быстрых событий генератора
    // списка старый callback не должен перетирать уже выбранную вкладку.
    private int _navPillUpdateVersion;

    private void OnCurrentSectionChangedForNavPill(int? _) => QueueNavPillUpdate(animate: true);

    private void QueueNavPillUpdate(bool animate)
    {
        var version = ++_navPillUpdateVersion;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (version != _navPillUpdateVersion || !IsLoaded)
                {
                    return;
                }

                UpdateNavPill(animate);
            }));
    }

    // Позиция общего индикатора NavPill: по левому краю ListBoxItem,
    // соответствующего CurrentSection, по центру вертикали. Источник истины
    // намеренно не SidebarList.SelectedItem: при TwoWay binding и перестроении
    // сгруппированного списка SelectedItem некоторое время может указывать на
    // предыдущую вкладку. Так индикатор физически следует за активной вкладкой.
    private void UpdateNavPill(bool animate)
    {
        var selectedSection = _viewModel.CurrentSection;
        if (selectedSection is null)
        {
            NavPill.Opacity = 0;
            return;
        }

        var item = SidebarList.ItemContainerGenerator.ContainerFromItem(selectedSection) as ListBoxItem;
        if (item is null || !item.IsLoaded || item.ActualHeight <= 0)
        {
            NavPill.Opacity = 0;
            return;
        }

        Point origin;
        try
        {
            origin = item.TranslatePoint(new Point(0, 0), NavListHost);
        }
        catch (InvalidOperationException)
        {
            // Элементы ещё не в одном визуальном дереве (перестройка списка).
            QueueNavPillUpdate(animate: false);
            return;
        }

        NavPillTranslate.X = origin.X;
        var targetY = origin.Y + (item.ActualHeight - NavPill.Height) / 2;

        // Плавный переезд — только при фактической смене вкладки; скролл/
        // перевёрстка должны мгновенно следовать за тем же пунктом.
        var fromY = NavPillTranslate.Y;
        if (!animate || !UiAnimations.Enabled || Math.Abs(fromY - targetY) < 0.5)
        {
            NavPillTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            NavPillTranslate.Y = targetY;
            NavPill.Opacity = 1;
            return;
        }

        NavPill.Opacity = 1;
        NavPillTranslate.Y = targetY;
        NavPillTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(fromY, targetY, new Duration(TimeSpan.FromMilliseconds(UiAnimations.SectionMs)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
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
            0 => CreateDashboardView(viewModel),
            1 => new Views.Sections.InfoView { DataContext = viewModel.Info },
            2 => new Views.Sections.ComponentsView { DataContext = viewModel.Components },
            3 => new Views.Sections.CleanupView { DataContext = viewModel.Cleanup },
            26 => new Views.Sections.AppCacheView { DataContext = viewModel.AppCache },
            4 => new Views.Sections.BloatView { DataContext = viewModel.Bloat },
            5 => new Views.Sections.PrivacyView { DataContext = viewModel.Privacy },
            // Раздел 27 «Windows Defender» — подвкладка группы «Приватность и защита».
            27 => new Views.Sections.DefenderView { DataContext = viewModel.Defender },
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
            25 => new Views.Sections.DriversView { DataContext = viewModel.Drivers },
            24 => new Views.Sections.DeepSeekView { DataContext = viewModel.DeepSeek },
            23 => new Views.Sections.TroubleshootingView { DataContext = viewModel.Troubleshooting },
            >= 100 => new Views.Sections.CustomUtilitiesView
            {
                DataContext = viewModel.GetCustomSectionViewModel(number),
            },
            _ => new Views.Sections.InfoView { DataContext = viewModel.Info }
        };

        _sectionViews[number] = view;
        return view;
    }

    // «Главная»: фабрика закреплённых карточек замыкается на MainViewModel —
    // логика (ленивая инициализация раздела-владельца, warn на неизвестный id)
    // живёт в PinnedCardFactory и покрыта тестами.
    private Views.Sections.DashboardView CreateDashboardView(MainViewModel viewModel)
    {
        var view = new Views.Sections.DashboardView { DataContext = viewModel.Dashboard };
        view.SetPinnedCardFactory(cardId =>
            Views.Cards.PinnedCardFactory.Create(cardId, viewModel, _logger));
        return view;
    }

    // Закрытие главного окна. Порядок обязателен: base.OnClosing(e) поднимает
    // событие Closing, обработчик которого при включённом фоновом режиме
    // (App.RunInBackgroundOnClose) отменяет закрытие и сворачивает окно. Пока
    // e.Cancel не проверен, освобождать ресурсы нельзя — иначе процесс остаётся
    // жив, но UI, подписки и глобальные горячие клавиши уже мертвы,
    // и повторное закрытие окна снова отменяется (аудит, п. 1).
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _shutdown.ReleaseIfClosing(e.Cancel);
    }

    // Страховка: окно, закрытое без прохождения OnClosing (например, завершение
    // приложения), всё равно освобождает ресурсы — ровно один раз.
    protected override void OnClosed(EventArgs e)
    {
        _shutdown.Release();
        base.OnClosed(e);
    }

    private readonly WindowShutdown _shutdown;

    // Освобождение ресурсов окна выполняется один раз и только при фактическом
    // закрытии: при отмене (фоновый режим) окно возвращается в работу как есть.
    private void ReleaseResources()
    {
        Loaded -= OnLoaded;
        _viewModel.MenuApplied -= OnMenuApplied;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_sectionHighlight is { } highlight)
        {
            _viewModel.SectionHighlightRequested -= highlight.OnSectionHighlightRequested;
        }

        _viewModel.CurrentSectionChanged -= OnCurrentSectionChangedForNavPill;
        _viewModel.CurrentSectionChanged -= ShowSectionView;
        Interop.WorkAreaMaximize.Detach(this);
        if (_globalHotkeyRegistered && _hwndSource is not null)
        {
            Interop.GlobalHotkeys.Unregister(_hwndSource.Handle);
        }
        ThemeManager.ThemeApplied -= OnThemeApplied;
        ThemeManager.IconAccentChanged -= OnIconAccentChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
        // Кэш view разделов держит ссылки на экземпляры view (а через их
        // DataContext — на подчинённые VM). При закрытии окна он больше не
        // нужен: обрываем ссылки, чтобы кэш не корневил MainViewModel после
        // Dispose. Визуалы не выгружаем — окно закрывается целиком. Отдельно
        // от инвалидации пользовательских вкладок (OnMenuApplied): это очистка
        // при завершении, а не правка кэша в живом приложении.
        _sectionViews.Clear();
        _viewModel.Dispose();
    }
}
