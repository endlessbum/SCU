using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SCU.ViewModels.Sections;

namespace SCU.Views.Sections;

public partial class DashboardView : UserControl
{
    // Прокрутка главной — внешний ContentScroll из MainWindow (своего
    // ScrollViewer у раздела нет), у него и берём размер видимой зоны.
    private ScrollViewer? _hostScroll;

    public DashboardView()
    {
        InitializeComponent();

        // Активность поиска = клавиатурный фокус в поле: от него зависят
        // подсказки, затемнение фона и закрытие Popup при уходе из поля.
        SearchBox.GotKeyboardFocus += (_, _) => SearchVm().IsSearchFocused = true;
        SearchBox.LostKeyboardFocus += (_, _) => SearchVm().IsSearchFocused = false;
        SearchBox.PreviewKeyDown += OnSearchBoxKeyDown;

        ToggleCore.SizeChanged += (_, _) => UpdateToggleBlockOffset();
        Loaded += (_, _) =>
        {
            HookHostScroll();
            UpdateSearchFieldOffset();
        };
        Unloaded += (_, _) => UnhookHostScroll();
    }

    // П. №7 UI-аудита: верхний отступ поля поиска раньше был константой 57.2,
    // выведенной из метрик шрифта шапки раздела (26 + 14 pt). Константа разъедалась
    // при смене шрифта/масштаба. Теперь измеряем реальной высотой строк тем же
    // FormattedText, что рендерит WPF: title + 4 + описание — выравнивание с
    // разделом, где шапка видима, сохраняется при любых настройках.
    private void UpdateSearchFieldOffset()
    {
        const double gap = 4;
        var offset = 57.2; // запасной вариант, если стили недоступны
        if (TryMeasureLine("PageTitleText", out var titleHeight)
            && TryMeasureLine("PageSubtitleText", out var subtitleHeight))
        {
            offset = Math.Round(titleHeight + gap + subtitleHeight, 1);
        }

        SearchHost.Margin = new Thickness(0, offset, 0, 0);
    }

    private bool TryMeasureLine(string styleKey, out double height)
    {
        height = 0;
        if (TryFindResource(styleKey) is not Style style)
        {
            return false;
        }

        double? fontSize = null;
        var weight = FontWeights.Normal;
        foreach (var setter in style.Setters.OfType<Setter>())
        {
            if (setter.Property == TextBlock.FontSizeProperty && setter.Value is double size)
            {
                fontSize = size;
            }
            else if (setter.Property == TextBlock.FontWeightProperty && setter.Value is FontWeight fontWeight)
            {
                weight = fontWeight;
            }
        }

        if (fontSize is not double concreteSize)
        {
            return false;
        }

        var typeface = new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        var formatted = new FormattedText(
            "Ag",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            concreteSize,
            null, // цвет для измерения высоты строки не нужен
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        height = formatted.Height;
        return true;
    }

    private DashboardViewModel SearchVm() =>
        (DashboardViewModel)DataContext;

    // Большой выключатель закреплён: верхний отступ блока центрирует его
    // постоянную часть (ToggleCore) в видимой зоне. Развёрнутый список
    // утилит и строки статуса растягивают блок вниз — выключатель не движется.
    private void UpdateToggleBlockOffset()
    {
        const double bottomLift = 48;
        const double minOffset = 16;
        if (_hostScroll is null)
        {
            return;
        }

        double zone = _hostScroll.ViewportHeight - SearchHost.ActualHeight;
        double offset = Math.Max(minOffset, (zone - ToggleCore.ActualHeight - bottomLift) / 2);
        ToggleBlock.Margin = new Thickness(0, offset, 0, bottomLift);
    }

    private void HookHostScroll()
    {
        DependencyObject? current = ToggleBlock;
        while (current is not null && current is not ScrollViewer)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        var host = current as ScrollViewer;
        if (ReferenceEquals(_hostScroll, host))
        {
            UpdateToggleBlockOffset();
            return;
        }

        if (_hostScroll is not null)
        {
            _hostScroll.SizeChanged -= OnHostScrollSizeChanged;
        }

        _hostScroll = host;
        if (_hostScroll is not null)
        {
            _hostScroll.SizeChanged += OnHostScrollSizeChanged;
        }

        UpdateToggleBlockOffset();
    }

    private void UnhookHostScroll()
    {
        if (_hostScroll is null)
        {
            return;
        }

        _hostScroll.SizeChanged -= OnHostScrollSizeChanged;
        _hostScroll = null;
    }

    // Появление/исчезновение вертикальной полосы прокрутки меняет только
    // ширину видимой зоны, поэтому реагируем на изменение высоты — иначе
    // выключатель сдвигался бы при каждом раскрытии списка.
    private void OnHostScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.PreviousSize.Height - e.NewSize.Height) > 0.5)
        {
            UpdateToggleBlockOffset();
        }
    }

    // Esc: спрятать подсказки → очистить запрос → снять фокус (закрыть поиск).
    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        var vm = SearchVm();
        if (vm.IsSearchSuggestionsOpen)
        {
            vm.IsSearchSuggestionsOpen = false;
        }
        else if (!string.IsNullOrEmpty(vm.SearchText))
        {
            vm.SearchText = string.Empty;
        }
        else
        {
            SearchBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        e.Handled = true;
    }

    // Прокрутка подсказок колесом. Попап — отдельное окно (HWND): сообщение
    // колесо уходит сфокусированному окну (MainWindow), минуя содержимое
    // попапа, поэтому обычная прокрутка ScrollViewer не срабатывала.
    // PreviewMouseWheel ловит доставку внутрь попапа, а недошедшие события
    // форвардит MainWindow.OnContentScrollPreviewMouseWheel.
    public void ScrollSuggestionsBy(double delta)
    {
        SuggestionsScroll.ScrollToVerticalOffset(SuggestionsScroll.VerticalOffset - delta);
    }

    private void OnSuggestionsScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollSuggestionsBy(e.Delta);
        e.Handled = true;
    }

    // Бегущая по периметру полоса большого выключателя: доли штрихов считаются
    // от фактического периметра скруглённого прямоугольника — штрих занимает
    // 12% периметра, зазор — остальное. Раньше значения (46.7 342.2) были
    // посчитаны вручную под конкретный размер 301×154 и ломались при его изменении.
    private void OnBatchRingSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Rectangle ring || ring.ActualHeight <= 0)
        {
            return;
        }

        var radius = Math.Min(ring.RadiusX, ring.ActualHeight / 2);
        var straight = 2 * (ring.ActualWidth - 2 * radius) + 2 * (ring.ActualHeight - 2 * radius);
        var perimeter = straight + 2 * Math.PI * radius;
        var units = perimeter / ring.StrokeThickness;
        const double dashFraction = 0.12;
        ring.StrokeDashArray = new DoubleCollection
        {
            dashFraction * units,
            (1.0 - dashFraction) * units,
        };
    }
}
