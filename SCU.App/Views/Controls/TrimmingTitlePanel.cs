using System.Windows;
using System.Windows.Controls;

namespace SCU.Views.Controls;

// Панель заголовка строки «название + ⓘ»: название прижато влево, глиф встаёт
// сразу после фактического конца текста; длинное название обрезается
// (TextTrimming у первого ребёнка), глиф остаётся видимым у правого края.
//
// Почему не существующие контейнеры (история — аудит п. 17):
//  - горизонтальный StackPanel: бесконечная ширина измерения ломала
//    TextTrimming, длинное название перекрывало правую колонку;
//  - Grid (звезда + Auto): обрезку вернул, но глиф встал у правого края
//    колонки — ⓘ уходил от короткого названия к правой части карточки;
//  - DockPanel: элемент, пристыкованный после обрезаемого текста, при
//    длинном названии выдавливается в нулевую ширину — ⓘ исчезает ровно
//    тогда, когда он нужнее всего.
// Дети фиксированы разметкой: [0] — обрезаемый заголовок, [1] — глиф.
public sealed class TrimmingTitlePanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        // Глиф резервируется до измерения текста: обрезка считается по остатку
        // ширины, иначе ⓘ выталкивался за пределы карточки.
        var glyph = Children.Count > 1 ? Children[1] : null;
        if (glyph is not null)
        {
            glyph.Measure(availableSize);
        }

        var textWidth = availableSize.Width;
        if (!double.IsInfinity(textWidth) && glyph is not null)
        {
            textWidth = Math.Max(0, textWidth - glyph.DesiredSize.Width);
        }

        var text = Children[0];
        text.Measure(new Size(textWidth, availableSize.Height));

        var width = text.DesiredSize.Width + (glyph?.DesiredSize.Width ?? 0);
        var height = Math.Max(text.DesiredSize.Height, glyph?.DesiredSize.Height ?? 0);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var text = Children[0];
        var glyph = Children.Count > 1 ? Children[1] : null;

        // Вертикальное центрирование: глиф может быть выше строки заголовка.
        var textRect = new Rect(
            0,
            (finalSize.Height - text.DesiredSize.Height) / 2,
            text.DesiredSize.Width,
            text.DesiredSize.Height);
        text.Arrange(textRect);

        if (glyph is not null)
        {
            // Позиция — по фактической ширине текста: после короткого названия
            // глиф прижат к тексту, после обрезанного — к многоточию.
            glyph.Arrange(new Rect(
                textRect.Right,
                (finalSize.Height - glyph.DesiredSize.Height) / 2,
                glyph.DesiredSize.Width,
                glyph.DesiredSize.Height));
        }

        return finalSize;
    }
}
