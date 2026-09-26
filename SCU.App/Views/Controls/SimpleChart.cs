using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SCU.Services.Dashboard;

namespace SCU.Views.Controls;

// Режим отображения: ломаная с точками или столбиковая диаграмма.
public enum SimpleChartMode
{
    Line,
    Bar
}

// Формат подписей значений: Number — «как есть» (например, гигабайты серии диска);
// Bytes — авторазмер КБ/МБ/ГБ (байты очисток).
public enum SimpleChartValueKind
{
    Number,
    Bytes
}

// Минимальный график без сторонних пакетов (§34 ТЗ): Canvas + Polyline/Rectangle.
// Только отображение реально переданных точек: без интерактивности, без зума,
// фиксированная высота задаётся в разметке. Значения приходят из ViewModel —
// контрол ничего не читает из системы сам.
public sealed class SimpleChart : Canvas
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<DataPoint>),
        typeof(SimpleChart),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode),
        typeof(SimpleChartMode),
        typeof(SimpleChart),
        new PropertyMetadata(SimpleChartMode.Line, OnVisualChanged));

    public static readonly DependencyProperty ValueKindProperty = DependencyProperty.Register(
        nameof(ValueKind),
        typeof(SimpleChartValueKind),
        typeof(SimpleChart),
        new PropertyMetadata(SimpleChartValueKind.Number, OnVisualChanged));

    // Кисти — через ссылки на ресурсы темы: при подмене словаря Colors.* ThemeManager'ом
    // значения свойств обновляются и график перерисовывается сам (OnVisualChanged).
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(SimpleChart),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register(
        nameof(AxisBrush),
        typeof(Brush),
        typeof(SimpleChart),
        new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush),
        typeof(Brush),
        typeof(SimpleChart),
        new PropertyMetadata(null, OnVisualChanged));

    public SimpleChart()
    {
        ClipToBounds = true;
        SetResourceReference(AccentBrushProperty, "AccentFillBrush");
        SetResourceReference(AxisBrushProperty, "SeparatorBrush");
        SetResourceReference(LabelBrushProperty, "SecondaryTextBrush");
        Loaded += (_, _) => Render();
    }

    public IReadOnlyList<DataPoint>? Points
    {
        get => (IReadOnlyList<DataPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public SimpleChartMode Mode
    {
        get => (SimpleChartMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public SimpleChartValueKind ValueKind
    {
        get => (SimpleChartValueKind)GetValue(ValueKindProperty);
        set => SetValue(ValueKindProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush? AxisBrush
    {
        get => (Brush?)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public Brush? LabelBrush
    {
        get => (Brush?)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Render();
    }

    private static void OnVisualChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((SimpleChart)sender).Render();
    }

    private void Render()
    {
        Children.Clear();
        var points = Points;
        if (points is null || points.Count == 0)
        {
            return;
        }

        var width = ActualWidth;
        var height = ActualHeight;
        if (double.IsNaN(width) || double.IsNaN(height) || width < 60 || height < 30)
        {
            // Размер ещё не разложен — перерисуемся по Loaded/SizeChanged.
            return;
        }

        const double leftPad = 54;   // место под подписи min/max слева
        const double rightPad = 8;
        const double topPad = 8;
        const double bottomPad = 16; // место под даты
        var plotWidth = Math.Max(10, width - leftPad - rightPad);
        var plotHeight = Math.Max(10, height - topPad - bottomPad);

        var min = points.Min(point => point.Value);
        var max = points.Max(point => point.Value);
        // Плоская серия (все значения равны): рисуем линию посередине, делитель = 1.
        if (Math.Abs(max - min) < 0.0001)
        {
            max = min + 1;
        }

        // Кисти приходят из токенов темы через resource reference; пока ресурсы
        // не разрешены (нет дерева/темы) — рисовать нечего.
        var accent = AccentBrush;
        var axis = AxisBrush;
        var label = LabelBrush;
        if (accent is null || axis is null || label is null)
        {
            return;
        }

        // Hairline-оси: горизонталь по низу и вертикаль слева.
        AddAxis(leftPad, topPad + plotHeight, leftPad + plotWidth, topPad + plotHeight, axis);
        AddAxis(leftPad, topPad, leftPad, topPad + plotHeight, axis);

        if (Mode == SimpleChartMode.Line)
        {
            RenderLine(points, accent, leftPad, topPad, plotWidth, plotHeight, min, max);
        }
        else
        {
            RenderBars(points, accent, leftPad, topPad, plotWidth, plotHeight, min, max);
        }

        // Подписи min/max у вертикальной оси и первая/последняя дата под горизонтальной.
        AddLabel(FormatValue(max), 2, topPad - 4, label, 40, TextAlignment.Right);
        AddLabel(FormatValue(min), 2, topPad + plotHeight - 6, label, 40, TextAlignment.Right);
        AddLabel(points[0].Timestamp.ToString(AxisFormat, FormatCulture), leftPad, height - 14, label, null, TextAlignment.Left);
        AddLabel(
            points[^1].Timestamp.ToString(AxisFormat, FormatCulture),
            leftPad,
            height - 14,
            label,
            plotWidth,
            TextAlignment.Right);
    }

    private void RenderLine(
        IReadOnlyList<DataPoint> points,
        Brush accent,
        double left,
        double top,
        double plotWidth,
        double plotHeight,
        double min,
        double max)
    {
        var polyline = new Polyline
        {
            Stroke = accent,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        foreach (var (x, y) in EnumerateCoordinates(points, left, top, plotWidth, plotHeight, min, max))
        {
            polyline.Points.Add(new Point(x, y));
            // Точки данных: маленькие кружки поверх ломаной.
            var dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = accent
            };
            SetLeft(dot, x - 2);
            SetTop(dot, y - 2);
            Children.Add(dot);
        }

        Children.Add(polyline);
    }

    private void RenderBars(
        IReadOnlyList<DataPoint> points,
        Brush accent,
        double left,
        double top,
        double plotWidth,
        double plotHeight,
        double min,
        double max)
    {
        var slot = plotWidth / points.Count;
        var barWidth = Math.Max(2, slot * 0.7);
        var index = 0;
        foreach (var (x, _) in EnumerateCoordinates(points, left, top, plotWidth, plotHeight, min, max))
        {
            var barHeight = Math.Max(1, (points[index].Value - min) / (max - min) * plotHeight);
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = accent,
                RadiusX = 1,
                RadiusY = 1
            };
            SetLeft(bar, x - barWidth / 2);
            SetTop(bar, top + plotHeight - barHeight);
            Children.Add(bar);
            index++;
        }
    }

    // Нормированные координаты точек внутри области построения.
    private static IEnumerable<(double X, double Y)> EnumerateCoordinates(
        IReadOnlyList<DataPoint> points,
        double left,
        double top,
        double plotWidth,
        double plotHeight,
        double min,
        double max)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var x = points.Count == 1
                ? left + plotWidth / 2
                : left + i / (double)(points.Count - 1) * plotWidth;
            var y = top + (1 - (points[i].Value - min) / (max - min)) * plotHeight;
            yield return (x, y);
        }
    }

    private void AddAxis(double x1, double y1, double x2, double y2, Brush brush)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = 1,
            SnapsToDevicePixels = true
        };
        Children.Add(line);
    }

    private void AddLabel(
        string text,
        double x,
        double y,
        Brush brush,
        double? width,
        TextAlignment alignment)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = brush,
            TextAlignment = alignment
        };
        if (width is { } w)
        {
            textBlock.Width = w;
        }

        SetLeft(textBlock, x);
        SetTop(textBlock, y);
        Children.Add(textBlock);
    }

    // Единицы измерения — из словарей строк (рус/англ подменяются вместе с языком);
    // на случай отсутствия ключа — англоязычный fallback.
    private string Unit(string key, string fallback) =>
        TryFindResource(key) as string ?? fallback;

    // Presentation-форматирование: числа и даты выводятся в локали пользователя
    // (RU — запятая в дробях и «dd.MM HH:mm», EN — точка). Сортировка, вычисления
    // и разбор данных здесь не участвуют.
    private static CultureInfo FormatCulture => CultureInfo.CurrentCulture;

    private static string AxisFormat =>
        string.Equals(FormatCulture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase)
            ? "dd.MM HH:mm"
            : "MM-dd HH:mm";

    private string FormatValue(double value)
    {
        if (ValueKind == SimpleChartValueKind.Bytes)
        {
            const double kb = 1024;
            const double mb = kb * 1024;
            const double gb = mb * 1024;
            return value >= gb
                ? (value / gb).ToString("0.#", FormatCulture) + " " + Unit("D_TrendsUnitGb", "GB")
                : value >= mb
                    ? (value / mb).ToString("0.#", FormatCulture) + " " + Unit("D_TrendsUnitMb", "MB")
                    : value >= kb
                        ? (value / kb).ToString("0.#", FormatCulture) + " " + Unit("D_TrendsUnitKb", "KB")
                        : Math.Round(value).ToString(FormatCulture) + " " + Unit("D_TrendsUnitBytes", "B");
        }

        return value.ToString("0.#", FormatCulture);
    }
}
