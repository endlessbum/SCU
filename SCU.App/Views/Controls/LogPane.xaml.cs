using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using SCU.ViewModels;

namespace SCU.Views.Controls;

public partial class LogPane : UserControl
{
    private const int MaxLines = 4000;

    private readonly List<string> _lines = [];
    private readonly Paragraph _paragraph = new();
    private readonly FlowDocument _document;
    private INotifyCollectionChanged? _observed;
    private bool _scrollPending;

    public LogPane()
    {
        _document = new FlowDocument(_paragraph);
        InitializeComponent();
        ConfigureDocument();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // Без переноса строк: горизонтальный скроллбар, как у консоли.
    // PageWidth = ∞ WPF не принимает, поэтому ширина страницы — ширина контрола
    // или самой длинной строки (оценка по моно-шрифту), что и даёт «без переноса».
    private void ConfigureDocument()
    {
        _document.PagePadding = new Thickness(0);
        _paragraph.Margin = new Thickness(0);
        _paragraph.LineHeight = double.NaN;
        LogTextBox.Document = _document;
        UpdatePageWidth();
        LogTextBox.SizeChanged += (_, _) => UpdatePageWidth();
    }

    private void UpdatePageWidth()
    {
        // Ширина символа моно-шрифта ≈ 0.62 em; оценка достаточна — цель лишь
        // «строка не переносится», точная подгонка не нужна.
        var widest = _lines.Count == 0 ? 0 : _lines.Max(line => line.Length) * LogTextBox.FontSize * 0.62;
        _document.PageWidth = Math.Max(LogTextBox.ActualWidth, widest + 24);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach(DataContext);
        ScrollToEnd(force: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Attach(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Attach(e.NewValue);
        ScrollToEnd(force: true);
    }

    private void Attach(object? context)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnLogLinesChanged;
            _observed = null;
        }

        if (context is MainViewModel viewModel)
        {
            _observed = viewModel.LogLines;
            _observed.CollectionChanged += OnLogLinesChanged;
            RebuildAll();
        }
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            AppendNewLines(e);
            QueueScrollToEnd();
            return;
        }

        Dispatcher.Invoke(() =>
        {
            AppendNewLines(e);
            QueueScrollToEnd();
        });
    }

    private void AppendNewLines(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            RebuildAll();
            return;
        }

        foreach (var item in e.NewItems!.Cast<string>())
        {
            AppendLine(item);
        }

        TrimIfNeeded();
        UpdatePageWidth();
    }

    private void AppendLine(string line)
    {
        _lines.Add(line);
        _paragraph.Inlines.Add(CreateRun(line));
        _paragraph.Inlines.Add(new LineBreak());
    }

    private void RebuildAll()
    {
        _lines.Clear();
        _paragraph.Inlines.Clear();
        if (DataContext is MainViewModel viewModel)
        {
            foreach (var line in viewModel.LogLines)
            {
                AppendLine(line);
            }
        }

        TrimIfNeeded();
        UpdatePageWidth();
    }

    // Цвет строки — по уровню из префикса Logger. Вывод внешних процессов
    // (SCU.ps1, DISM, SFC) идёт без « APP | » — приглушённым цветом, чтобы
    // собственные статусы приложения читались на его фоне.
    private static Run CreateRun(string line)
    {
        var run = new Run(line);
        if (line.Contains(" APP | ERROR | ", StringComparison.Ordinal))
        {
            run.SetResourceReference(TextElement.ForegroundProperty, "DangerFillBrush");
            run.FontWeight = FontWeights.SemiBold;
        }
        else if (line.Contains(" APP | WARN | ", StringComparison.Ordinal))
        {
            run.SetResourceReference(TextElement.ForegroundProperty, "WarnBrush");
        }
        else if (line.Contains(" APP | ", StringComparison.Ordinal))
        {
            run.SetResourceReference(TextElement.ForegroundProperty, "SecondaryTextBrush");
        }
        else
        {
            run.SetResourceReference(TextElement.ForegroundProperty, "TertiaryTextBrush");
        }

        return run;
    }

    private void TrimIfNeeded()
    {
        // Удаление с головы: первые два inline — Run + LineBreak старейшей строки.
        // Полная пересборка на каждую строку при потоке тысяч строк (DISM/SFC) — O(n²).
        while (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
            var first = _paragraph.Inlines.FirstInline;
            if (first is null)
            {
                break;
            }

            _paragraph.Inlines.Remove(first);
            var second = _paragraph.Inlines.FirstInline;
            if (second is not null)
            {
                _paragraph.Inlines.Remove(second);
            }
        }
    }

    // Прокрутку откладываем до конца очереди диспетчера: синхронный ScrollToEnd внутри
    // CollectionChanged форсирует вёрстку посреди обновления коллекции. Если в этот момент
    // вёрстка встречает исключение (например, в шаблоне карточки дашборда), оно уходит в
    // глобальный обработчик, где MessageBox раскручивает вложенный pump — и каскад стопорит
    // приложение. Отложка разрывает эту связь, флаг не даёт наслаивать вызовы при пачке строк.
    private void QueueScrollToEnd()
    {
        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _scrollPending = false;
                ScrollToEnd(force: false);
            }));
    }

    private void ScrollToEnd(bool force)
    {
        // Исключение вёрстки из прокрутки лога не должно улетать в глобальный обработчик:
        // прокрутка некритична, а падение здесь из CollectionChanged запускает каскад
        // MessageBox из вложенного pump диспетчера.
        try
        {
            // Пользователь, ушедший прокруткой в историю, не выдёргивается к хвосту
            // каждой новой строкой; вернулся к низу — автопрокрутка продолжается.
            if (force || IsAtBottom())
            {
                LogTextBox.ScrollToEnd();
            }
        }
        catch
        {
            // Подавляем: журнал продолжит наполняться без автопрокрутки.
        }
    }

    private bool IsAtBottom()
    {
        var tail = LogTextBox.ExtentHeight - LogTextBox.VerticalOffset - LogTextBox.ViewportHeight;
        return tail < 8 || double.IsNaN(tail);
    }
}
