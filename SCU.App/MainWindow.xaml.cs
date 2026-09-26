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
    private const int WmGetMinMaxInfo = 0x0024;

    private readonly MainViewModel _viewModel;
    private readonly Logger _logger;

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
        _viewModel.SectionHighlightRequested += OnSectionHighlightRequested;

        // Выделение текста мышью в любом месте окна: протягивание подсвечивает только
        // выделенные символы (как в редакторе), Ctrl+C — копировать, Ctrl+A — выделить всё.
        // Засвет целой строки нет, курсор не меняется; интерактивные элементы не затрагиваются.
        AddHandler(TextBlock.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnTextMouseDown), true);
        AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler(OnAnyMouseDown), true);
        AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler(OnTextMouseMove), true);
        AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnTextMouseUp), true);
    }

    // ===================== Подсветка элемента после перехода по ссылке =====================

    // Контур, играющий сейчас (новая вспышка снимает предыдущую,
    // чтобы рамки не наслаивались при быстрых переходах).
    private Views.Controls.SearchOutlineAdorner? _outlineAdorner;

    private void OnSectionHighlightRequested(int sectionNumber, string? elementTitle) =>
        // Навигация меняет видимость вкладок биндингами; поиск элемента и контур —
        // по уже показанному и разложенному разделу, поэтому после Layout.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FlashSectionOutline(elementTitle));

    // Пунктирный контур элемента, к которому привёл переход: по заголовку
    // из подсказки поиска находится строка в открывшемся разделе и подсвечивается
    // её карточка. Если строки нет (переход без конкретного элемента, например из
    // бэнчмарка) — подсвечивается пункт меню раздела.
    private void FlashSectionOutline(string? elementTitle)
    {
        if (!string.IsNullOrEmpty(elementTitle) && FindRowCardByText(elementTitle) is { } card)
        {
            // Прокрутка к строке, затем отступ от верхнего края: карточка,
            // прижатая к границе окна, обрезала бы контур.
            card.BringIntoView();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                ContentScroll.ScrollToVerticalOffset(Math.Max(0, ContentScroll.VerticalOffset - OutlineScrollMargin));
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => StartOutline(card));
            });
            return;
        }

        FlashSidebarOutline();
    }

    // Контур выступает за границы карточки на полпикселя; столько пикселей
    // отводим сверху и снизу при прокрутке, чтобы рамка была видна целиком.
    private const double OutlineScrollMargin = 8;

    // Ищет в открытом разделе карточку, в которой лежит заголовок утилиты из
    // подсказки. Совпадение сначала точное; если раздел называет параметр иначе
    // («Отключить гибернацию» → «Гибернация»), ищем по основе слова. Из
    // кандидатов выбирается самый точный.
    private FrameworkElement? FindRowCardByText(string title)
    {
        if (ContentScroll.Content is not DependencyObject root)
        {
            return null;
        }

        TextBlock? best = null;
        var bestScore = -1;
        Walk(root);
        return best is null ? null : PickRowCard(best, title);

        void Walk(DependencyObject node)
        {
            if (node is TextBlock block
                && block.IsVisible
                && block.Visibility == Visibility.Visible)
            {
                var score = MatchScore(title, block.Text);
                if (score > bestScore)
                {
                    best = block;
                    bestScore = score;
                }
            }

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private const int MatchThreshold = 4;

    // 3 — точное совпадение; 1–2 — совпадение по основе слова (не менее 4 символов
    // в основе, без учёта падежных окончаний); 0 — не совпало.
    private static int MatchScore(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (string.Equals(title.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        var stem = Stem(title);
        if (stem.Length < MatchThreshold)
        {
            return 0;
        }

        var normalized = text.Trim().ToLowerInvariant();
        if (normalized.Contains(stem, StringComparison.Ordinal)
            || stem.StartsWith(normalized, StringComparison.Ordinal))
        {
            return 2;
        }

        return 0;
    }

    // Основа для поиска: глагол действия («Отключить», «Включить», «Очистить»…)
    // отбрасывается, из остатка берётся начало — падежные окончания не важны.
    private static string Stem(string title)
    {
        var words = title.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var content = words.FirstOrDefault(word
            => word is not ("отключить" or "включить" or "установить" or "очистить" or "сбросить"
                or "задать" or "применить" or "запустить" or "создать" or "удалить" or "восстановить"));
        if (content is null)
        {
            return string.Empty;
        }

        return content.Length <= 6 ? content : content[..6];
    }

    // От заголовка строки поднимается к ближайшей подходящей карточке.
    private static FrameworkElement PickRowCard(TextBlock block, string title)
    {
        FrameworkElement? candidate = null;
        DependencyObject current = block;
        while (current is not null)
        {
            if (current is Border border
                && border.ActualHeight >= 44
                && border.ActualHeight <= 420)
            {
                candidate = border;
                break;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        // Карточка не найдена (строка без своего Border) — подсвечиваем сам
        // контейнер строки; не нашли и его — сам заголовок.
        return candidate
            ?? (VisualTreeHelper.GetParent(block) as FrameworkElement)
            ?? block;
    }

    // Свечение пункта меню: заголовок раздела всегда виден в сайдбаре.
    private void FlashSidebarOutline()
    {
        var section = _viewModel.CurrentSection;
        if (section is null)
        {
            return;
        }

        SidebarList.ScrollIntoView(section);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (SidebarList.ItemContainerGenerator.ContainerFromItem(section) is ListBoxItem item)
            {
                StartOutline(item);
            }
        });
    }

    // Одноразовый бегущий контур: секунда мягкого появления (рамка уже движется),
    // две секунды удержания, полсекунды исчезновения; эффект не остаётся на
    // постоянной основе.
    private void StartOutline(UIElement target)
    {
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer)
        {
            return;
        }

        if (_outlineAdorner is { } previous)
        {
            if (AdornerLayer.GetAdornerLayer(previous.AdornedElement) is { } previousLayer)
            {
                previousLayer.Remove(previous);
            }
            _outlineAdorner = null;
        }

        var brush = TryFindResource("AccentFillBrush") as Brush ?? Brushes.DodgerBlue;
        var outline = new Views.Controls.SearchOutlineAdorner(target, brush);
        layer.Add(outline);
        _outlineAdorner = outline;

        // Бегущий пунктир: смещение штриха за один период узора (6+4). Смещение
        // растёт — штрихи бегут против часовой стрелки (проверено рендером).
        // Один цикл — 0.7 с: движение заметное, но мягкое.
        var march = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(700))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        outline.Outline.BeginAnimation(System.Windows.Shapes.Rectangle.StrokeDashOffsetProperty, march);

        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(3500) };
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3500)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        });
        flash.Completed += (_, _) =>
        {
            layer.Remove(outline);
            if (ReferenceEquals(_outlineAdorner, outline))
            {
                _outlineAdorner = null;
            }
        };
        outline.BeginAnimation(OpacityProperty, flash);
    }

    // Mica, скругление углов и тёмный режим DWM. При недоступности бэкдропа —
    // непрозрачный фон из словаря темы вместо прозрачного окна.
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);

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

    // Разворачивание строго в рабочую область монитора, на котором стоит окно
    // (не накрывает панель задач). SystemParameters.WorkArea — только первичный
    // монитор: на втором мониторе maximise уезжал бы в чужие координаты.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };
        var workArea = GetMonitorInfo(monitor, ref monitorInfo)
            ? monitorInfo.rcWork
            : new RECT
            {
                Left = (int)SystemParameters.WorkArea.Left,
                Top = (int)SystemParameters.WorkArea.Top,
                Right = (int)SystemParameters.WorkArea.Right,
                Bottom = (int)SystemParameters.WorkArea.Bottom
            };

        info.ptMaxPosition = new POINT { X = workArea.Left, Y = workArea.Top };
        info.ptMaxSize = new POINT { X = workArea.Right - workArea.Left, Y = workArea.Bottom - workArea.Top };

        // Минимальный размер окна: без этого ptMinTrackSize в обрабатываемой
        // структуре остаётся нулём и отключает проверку MinWidth/MinHeight —
        // окно сжимается почти в точку. Значения XAML в DIU переводятся в
        // физические пиксели по текущему DPI.
        var dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                  ?? Matrix.Identity;
        info.ptMinTrackSize = new POINT
        {
            X = (int)Math.Ceiling(MinWidth * dpi.M11),
            Y = (int)Math.Ceiling(MinHeight * dpi.M22)
        };

        Marshal.StructureToPtr(info, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

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

    // ===================== Выделение и копирование текста =====================

    private const double DragThreshold = 4;

    private Point _selectionStartPoint;
    private bool _isSelecting;

    // Текущая подсветка: блок → снимок исходного состояния (Инлайны + значение Text).
    // Пересоздание Run-ов по тексту убивает привязки и DynamicResource, поэтому в
    // снимке хранятся ИСХОДНЫЕ Inline-объекты и локальное значение свойства Text
    // (биндинг/мультибиндинг/DynamicResource/строка) — при снятии выделения они
    // возвращаются теми же объектами и привязки продолжают работать.
    private readonly Dictionary<TextBlock, HighlightSnapshot> _highlights = [];

    private sealed class HighlightSnapshot
    {
        public required List<Inline> Inlines { get; init; }

        public required object? TextValue { get; init; }

        public required string Text { get; init; }

        public int Start { get; set; }

        public int Length { get; set; }
    }

    private void OnTextMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBlock block || !IsSelectableText(block))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            // Двойной клик — выделить блок целиком.
            _isSelecting = false;
            ClearSelection();
            HighlightBlock(block, block.Text, 0, block.Text.Length);
            e.Handled = true;
            return;
        }

        ClearSelection();
        _isSelecting = true;
        _selectionStartPoint = e.GetPosition(this);
    }

    // Клик мимо выбираемого текста снимает выделение (стандартное поведение).
    private void OnAnyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSelecting)
        {
            return;
        }

        if (e.OriginalSource is TextBlock block && IsSelectableText(block))
        {
            return;
        }

        ClearSelection();
    }

    private void OnTextMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if ((current - _selectionStartPoint).Length <= DragThreshold)
        {
            return;
        }

        ApplySelection(new Rect(_selectionStartPoint, current));
    }

    private void OnTextMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        _isSelecting = false;
        // Простой клик без протягивания — выделение снимается.
        if ((e.GetPosition(this) - _selectionStartPoint).Length <= DragThreshold)
        {
            ClearSelection();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Журнал (TextBox) обрабатывает Ctrl+C / Ctrl+A сам.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
        {
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var text = GetSelectedText();
            if (!string.IsNullOrEmpty(text))
            {
                CopyText(text);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SelectAll();
            e.Handled = true;
        }
    }

    // Пересчёт подсветки по прямоугольнику протягивания: выделяются только те символы,
    // чьи знакоместа пересекают прямоугольник.
    private void ApplySelection(Rect rect)
    {
        var matched = new List<(TextBlock Block, string Text, int Start, int Length)>();
        foreach (var block in EnumerateTextBlocks(this))
        {
            if (!IsSelectableText(block))
            {
                continue;
            }

            if (TryGetSelectedRange(block, rect, out var text, out var start, out var length))
            {
                matched.Add((block, text, start, length));
            }
        }

        var matchedBlocks = new HashSet<TextBlock>(matched.Select(m => m.Block));
        foreach (var block in _highlights.Keys.ToList())
        {
            if (!matchedBlocks.Contains(block))
            {
                RestoreBlock(block);
            }
        }

        foreach (var (block, text, start, length) in matched)
        {
            HighlightBlock(block, text, start, length);
        }
    }

    private void SelectAll()
    {
        ClearSelection();
        foreach (var block in EnumerateTextBlocks(this))
        {
            if (IsSelectableText(block))
            {
                HighlightBlock(block, block.Text, 0, block.Text.Length);
            }
        }
    }

    // Диапазон символов блока, попадающих в прямоугольник выделения (в координатах окна).
    private bool TryGetSelectedRange(TextBlock block, Rect rect, out string text, out int start, out int length)
    {
        text = string.Empty;
        start = 0;
        length = 0;

        text = block.Text;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var toWindow = block.TransformToAncestor(this);
        var bounds = new Rect(toWindow.Transform(new Point(0, 0)), block.RenderSize);
        if (!bounds.IntersectsWith(rect))
        {
            return false;
        }

        var rectInBlock = toWindow.Inverse.TransformBounds(rect);
        var contentStart = block.ContentStart;

        var first = -1;
        var last = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var position = contentStart.GetPositionAtOffset(i, LogicalDirection.Forward);
            if (position is null)
            {
                break;
            }

            var charRect = position.GetCharacterRect(LogicalDirection.Forward);
            if (charRect.IsEmpty)
            {
                continue;
            }

            if (charRect.IntersectsWith(rectInBlock))
            {
                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }
        }

        if (first < 0)
        {
            return false;
        }

        start = first;
        length = last - first + 1;
        return true;
    }

    private void HighlightBlock(TextBlock block, string text, int start, int length)
    {
        if (_highlights.TryGetValue(block, out var snapshot))
        {
            if (snapshot.Start == start && snapshot.Length == length)
            {
                return;
            }
        }
        else
        {
            // Снимок делается до пересборки Inlines: исходные Inline-объекты и
            // локальное значение Text сохраняются как есть — при восстановлении
            // они возвращаются теми же объектами, поэтому привязка Text,
            // MultiBinding и DynamicResource не «замирают».
            snapshot = new HighlightSnapshot
            {
                Inlines = block.Inlines.ToList(),
                TextValue = block.ReadLocalValue(TextBlock.TextProperty),
                Text = block.Text
            };
            _highlights[block] = snapshot;
        }

        var selectionBrush = (Brush)FindResource("TextSelectionBrush");
        (start, length) = SnapToCharBounds(text, start, length);
        block.Inlines.Clear();
        if (start > 0)
        {
            block.Inlines.Add(new Run(text[..start]));
        }

        block.Inlines.Add(new Run(text.Substring(start, length)) { Background = selectionBrush });
        if (start + length < text.Length)
        {
            block.Inlines.Add(new Run(text[(start + length)..]));
        }

        snapshot.Start = start;
        snapshot.Length = length;
    }

    // Границы диапазона не должны разрезать суррогатную пару: индексы приходят
    // из сопоставления прямоугольников и могут указать на половину символа.
    private static (int Start, int Length) SnapToCharBounds(string text, int start, int length)
    {
        if (start > 0
            && start < text.Length
            && char.IsHighSurrogate(text[start - 1])
            && char.IsLowSurrogate(text[start]))
        {
            start--;
            length++;
        }

        var end = start + length;
        if (end > start
            && end < text.Length
            && char.IsHighSurrogate(text[end - 1])
            && char.IsLowSurrogate(text[end]))
        {
            length++;
        }

        if (start + length > text.Length)
        {
            length = text.Length - start;
        }

        return (start, length);
    }

    private void RestoreBlock(TextBlock block)
    {
        if (!_highlights.Remove(block, out var snapshot))
        {
            return;
        }

        // Те же исходные Inline-объекты в том же порядке: сохраняются их свойства
        // и собственные биндинги уровня Run/Bold/Hyperlink.
        block.Inlines.Clear();
        foreach (var inline in snapshot.Inlines)
        {
            block.Inlines.Add(inline);
        }

        // Свойство TextBlock.Text: возвращаем сохранённое локальное значение.
        // Биндинги навешиваются заново (свежая копия того же биндинга), прочие
        // выражения (например, DynamicResource) перецепляются тем же объектом,
        // строка — записывается как была; UnsetValue — снимаем локальное значение.
        switch (snapshot.TextValue)
        {
            case System.Windows.Data.BindingExpression binding:
                block.SetBinding(TextBlock.TextProperty, binding.ParentBinding);
                break;
            case System.Windows.Data.MultiBindingExpression multiBinding:
                block.SetBinding(TextBlock.TextProperty, multiBinding.ParentMultiBinding);
                break;
            case System.Windows.Data.PriorityBindingExpression priorityBinding:
                block.SetBinding(TextBlock.TextProperty, priorityBinding.ParentPriorityBinding);
                break;
            case Expression:
                block.SetValue(TextBlock.TextProperty, snapshot.TextValue);
                break;
            default:
                if (Equals(snapshot.TextValue, DependencyProperty.UnsetValue))
                {
                    block.ClearValue(TextBlock.TextProperty);
                }
                else
                {
                    block.SetValue(TextBlock.TextProperty, snapshot.TextValue);
                }

                break;
        }
    }

    private void ClearSelection()
    {
        foreach (var block in _highlights.Keys.ToList())
        {
            RestoreBlock(block);
        }

        _highlights.Clear();
    }

    private string? GetSelectedText()
    {
        if (_highlights.Count == 0)
        {
            return null;
        }

        return string.Join(
            "\n",
            _highlights
                .OrderBy(kv => BlockOrigin(kv.Key).Y)
                .ThenBy(kv => BlockOrigin(kv.Key).X)
                .Select(kv => kv.Value.Text.Substring(kv.Value.Start, kv.Value.Length)));
    }

    private Point BlockOrigin(TextBlock block)
    {
        try
        {
            return block.TransformToAncestor(this).Transform(new Point(0, 0));
        }
        catch
        {
            return new Point(double.MaxValue, double.MaxValue);
        }
    }

    private static void CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Буфер обмена может быть занят другим процессом — молча пропускаем.
        }
    }

    private static bool IsSelectableText(TextBlock block)
    {
        // Не отбираем мышь у интерактивных элементов и их подписей.
        DependencyObject current = block;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.ComboBox
                or System.Windows.Controls.ComboBoxItem
                or System.Windows.Controls.ListBoxItem)
            {
                return false;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return !string.IsNullOrWhiteSpace(block.Text);
    }

    private static IEnumerable<TextBlock> EnumerateTextBlocks(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock textBlock)
            {
                yield return textBlock;
            }

            foreach (var nested in EnumerateTextBlocks(child))
            {
                yield return nested;
            }
        }
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
