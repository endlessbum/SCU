using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SCU.Views.Controls;

// Мягкое акцентное свечение элемента при переходе по ссылкам (подсказки
// поиска, бэнчмарк): три концентрических скруглённых контура с убывающей
// плотностью вокруг элемента. Кольца отстоят от границы элемента на зазор
// и рисуются целиком СНАРУЖИ — иначе боковые сегменты ложатся на край
// карточки и выглядят обрезанными. Яркость гаснет анимацией Opacity
// (её запускает MainWindow), по завершении адорнер снимается со слоя.
public sealed class GlowFlashAdorner : Adorner
{
    // Радиус скругления подсвечиваемых карточек (SectionCard/NavListItem).
    private const double BaseRadius = 12;

    // Зазор между границей элемента и началом свечения.
    private const double Gap = 5;

    public GlowFlashAdorner(UIElement adornedElement, Brush brush) : base(adornedElement)
    {
        _brush = brush;
        IsHitTestVisible = false;
    }

    private readonly Brush _brush;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(AdornedElement.RenderSize);
        // Внешний слой — шире и тусклее, внутренний — тоньше и ярче: суммарно
        // мягкий ореол без резкой границы.
        DrawRing(drawingContext, rect, thickness: 12, opacity: 0.12);
        DrawRing(drawingContext, rect, thickness: 7, opacity: 0.25);
        DrawRing(drawingContext, rect, thickness: 3, opacity: 0.55);
    }

    private void DrawRing(DrawingContext drawingContext, Rect rect, double thickness, double opacity)
    {
        // Прозрачность слоя — альфой кисти: Pen не имеет Opacity.
        var brush = _brush.Clone();
        brush.Opacity = opacity;
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();

        // Контур целиком снаружи: граница кольца начинается в Gap от элемента,
        // штрих уходит наружу ещё на thickness/2.
        var offset = Gap + thickness / 2;
        var ring = new Rect(
            rect.X - offset,
            rect.Y - offset,
            rect.Width + offset * 2,
            rect.Height + offset * 2);
        var radius = BaseRadius + offset;
        drawingContext.DrawRoundedRectangle(null, pen, ring, radius, radius);
    }
}
