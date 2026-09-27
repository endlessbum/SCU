using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SCU.Common;

// Стандартный логотип встроенного браузера (раздел 22): шар с линиями сети —
// окружность, вертикальный меридиан и экватор. Рисуется текущим акцентным
// цветом SCU: используется как значок окна браузера (панель задач, Alt+Tab)
// и глиф в панели инструментов. Перерисовывается при смене акцента в настройках.
public static class BrowserIconManager
{
    // Иконка окна: RenderTargetBitmap, готовая к Window.Icon.
    public static ImageSource CreateIcon(Color accent, int size = 64)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            DrawGlobe(context, new Size(size, size), accent);
        }

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawGlobe(DrawingContext context, Size size, Color accent)
    {
        var brush = new SolidColorBrush(accent);
        brush.Freeze();

        var stroke = size.Width / 12.0;
        var center = new Point(size.Width / 2.0, size.Height / 2.0);
        var radius = size.Width / 2.0 - stroke * 1.2;

        var pen = new Pen(brush, stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();

        context.DrawEllipse(null, pen, center, radius, radius);
        context.DrawEllipse(null, pen, center, radius * 0.45, radius);
        context.DrawLine(
            pen,
            new Point(center.X - radius, center.Y),
            new Point(center.X + radius, center.Y));
    }
}
