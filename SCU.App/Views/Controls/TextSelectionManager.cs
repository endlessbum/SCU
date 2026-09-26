namespace SCU.Views.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

// П. 13 аудита: подсистема выделения/копирования текста мышью и клавиатурой,
// вынесена из MainWindow. Один экземпляр на окно; конструктор регистрирует
// обработчики мыши, HandleKeyDown вызывается из окна для Ctrl+C / Ctrl+A.
internal sealed class TextSelectionManager
{
    private readonly Window _owner;

    public TextSelectionManager(Window owner)
    {
        _owner = owner;
        // Выделение текста мышью в любом месте окна: протягивание подсвечивает только
        // выделенные символы (как в редакторе), Ctrl+C — копировать, Ctrl+A — выделить всё.
        // Засвет целой строки нет, курсор не меняется; интерактивные элементы не затрагиваются.
        owner.AddHandler(TextBlock.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnTextMouseDown), true);
        owner.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler(OnAnyMouseDown), true);
        owner.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler(OnTextMouseMove), true);
        owner.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnTextMouseUp), true);
    }

    public void HandleKeyDown(KeyEventArgs e)
    {
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
        _selectionStartPoint = e.GetPosition(_owner);
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

        var current = e.GetPosition(_owner);
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
        if ((e.GetPosition(_owner) - _selectionStartPoint).Length <= DragThreshold)
        {
            ClearSelection();
        }
    }

    // Пересчёт подсветки по прямоугольнику протягивания: выделяются только те символы,
    // чьи знакоместа пересекают прямоугольник.
    private void ApplySelection(Rect rect)
    {
        var matched = new List<(TextBlock Block, string Text, int Start, int Length)>();
        foreach (var block in EnumerateTextBlocks(_owner))
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
        foreach (var block in EnumerateTextBlocks(_owner))
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

        var toWindow = block.TransformToAncestor(_owner);
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

        var selectionBrush = (Brush)_owner.FindResource("TextSelectionBrush");
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
            return block.TransformToAncestor(_owner).Transform(new Point(0, 0));
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
}
