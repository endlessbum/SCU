using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SCU.Views.Controls;

// Контур элемента, к которому привёл переход по ссылке (подсказки поиска,
// бэнчмарк): пунктирная рамка — точная копия контура drop-зоны раздела
// «Сканер» (радиус 14, толщина 1.5, штрих 6/4 акцентным цветом). Контур не
// статичен: MainWindow анимирует StrokeDashOffset, и штрихи бегут по периметру
// против часовой стрелки. Прозрачностью управляет анимация Opacity в MainWindow,
// по завершении адорнер снимается со слоя.
public sealed class SearchOutlineAdorner : Adorner
{
    public SearchOutlineAdorner(UIElement adornedElement, Brush brush) : base(adornedElement)
    {
        IsHitTestVisible = false;
        Outline = new Rectangle
        {
            Stroke = brush,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 6, 4 },
            StrokeDashCap = PenLineCap.Flat,
            StrokeDashOffset = 0,
            RadiusX = 14,
            RadiusY = 14,
        };
        AddVisualChild(Outline);
    }

    // Пунктирная рамка; её StrokeDashOffset анимирует MainWindow.
    public Rectangle Outline { get; }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => Outline;

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Отступ 1 — как у Rectangle в drop-зоне «Сканера»: линия штриха идёт
        // по центру и без отступа внешний полупиксель ложится на границу карточки.
        Outline.Arrange(new Rect(1, 1, Math.Max(0, finalSize.Width - 2), Math.Max(0, finalSize.Height - 2)));
        return finalSize;
    }
}
