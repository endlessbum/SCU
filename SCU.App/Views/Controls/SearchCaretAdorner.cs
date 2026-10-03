using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace SCU.Views.Controls;

/// <summary>
/// Кастомный текстовый курсор поля поиска. Нативный курсор WPF рисуется
/// ровно на левом крае следующего символа и наезжает на его штрих (у текста
/// 16 px боковой отступ глифа меньше пикселя) — выглядит «налипшим» на текст.
/// Здесь: нативный курсор скрыт (прозрачный CaretBrush), а этот адорнер рисует
/// линию на 1 px раньше позиции ввода. Мигание — как у системного: 500 мс,
/// с фазой сбросом при перемещении курсора.
/// </summary>
/// <remarks>
/// Производительность (диагностика тормозов поиска): прямоугольник курсора
/// кэшируется и пересчитывается ТОЛЬКО по реальным событиям (смена выделения,
/// текста, размера поля). OnRender не трогает TextBoxView вообще:
/// GetRectFromCharacterIndex внутри render-прохода форсирует форматирование
/// строки (DirectWrite), а подписка LayoutUpdated → InvalidateVisual (глобальное
/// событие — срабатывает после каждого прохода вёрстки окна) превращала это в
/// бесконечный цикл «вёрстка → инвалидация → рендер → форматирование»:
/// UI-поток замирал на 250–800 мс каждые полсекунды, пока поле в фокусе.
/// Таймер мигания — Background: мигание косметично и не должно приоритетнее ввода.
/// </remarks>
public sealed class SearchCaretAdorner : Adorner
{
    private const int CaretOffsetPixels = 1;

    private readonly TextBox _field;
    private readonly DispatcherTimer _blink;
    private Rect _caretRect;
    private bool _hasCaretRect;

    public SearchCaretAdorner(TextBox field) : base(field)
    {
        _field = field;
        IsHitTestVisible = false;

        _blink = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _blink.Tick += (_, _) => IsCaretVisible = !IsCaretVisible;

        _field.SelectionChanged += (_, _) => OnCaretMoved();
        _field.TextChanged += (_, _) => OnCaretMoved();
        _field.SizeChanged += (_, _) => OnCaretMoved();
        _field.GotKeyboardFocus += (_, _) => OnFocusChanged();
        _field.LostKeyboardFocus += (_, _) => OnFocusChanged();
    }

    private bool IsCaretVisible
    {
        get => _isCaretVisible;
        set
        {
            _isCaretVisible = value;
            InvalidateVisual();
        }
    }

    private bool _isCaretVisible;

    /// <summary>Подключение: спрятать нативный курсор и добавить адорнер (без дублей).</summary>
    public static void Attach(TextBox field)
    {
        field.CaretBrush = Brushes.Transparent;
        var layer = AdornerLayer.GetAdornerLayer(field);
        if (layer is null)
        {
            return;
        }

        // Повторный Loaded (возврат на вкладку) не должен наслаивать адорнеры.
        foreach (var existing in layer.GetAdorners(field)?.OfType<SearchCaretAdorner>().ToArray() ?? [])
        {
            layer.Remove(existing);
        }

        layer.Add(new SearchCaretAdorner(field));
    }

    private void OnFocusChanged()
    {
        // Фаза мигания начинается с видимого состояния.
        IsCaretVisible = true;
        if (_field.IsKeyboardFocusWithin)
        {
            _blink.Start();
        }
        else
        {
            _blink.Stop();
        }

        UpdateCaretRect();
        InvalidateVisual();
    }

    // Перемещение курсора перезапускает фазу: линия снова видима —
    // так курсор не «теряется» при наборе и стрелках.
    private void OnCaretMoved()
    {
        if (_field.IsKeyboardFocusWithin)
        {
            IsCaretVisible = true;
            _blink.Stop();
            _blink.Start();
        }

        UpdateCaretRect();
        InvalidateVisual();
    }

    private void UpdateCaretRect()
    {
        try
        {
            _caretRect = _field.GetRectFromCharacterIndex(_field.CaretIndex, false);
            _hasCaretRect = !double.IsNaN(_caretRect.X) && !double.IsInfinity(_caretRect.X);
        }
        catch
        {
            _hasCaretRect = false;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!_field.IsKeyboardFocusWithin
            || _field.IsReadOnly
            || _field.SelectionLength > 0
            || !IsCaretVisible
            || !_hasCaretRect)
        {
            return;
        }

        // Leading edge позиции курсора: левый край символа под курсором
        // (или точка сразу после последнего). Отступ — на пиксель раньше текста.
        var x = Math.Max(_caretRect.X - CaretOffsetPixels, 0);
        var brush = _field.Foreground;
        if (brush is not SolidColorBrush)
        {
            brush = Brushes.Gray;
        }

        // Чуть короче и жирнее системного: 2 px ширины, высота с отступом
        // сверху и снизу — линия центрируется в строке и не сливается с текстом.
        var height = Math.Max(_caretRect.Height - 5, 4);
        drawingContext.DrawRectangle(brush, null, new Rect(x, _caretRect.Y + 2.5, 2, height));
    }
}
