using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SCU.ViewModels;

namespace SCU.Views.Controls;

public partial class LogPane : UserControl
{
    private const int MaxLines = 4000;

    private readonly StringBuilder _text = new();
    private INotifyCollectionChanged? _observed;
    private bool _scrollPending;

    public LogPane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach(DataContext);
        ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Attach(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Attach(e.NewValue);
        ScrollToEnd();
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
            _text.AppendLine(item);
        }

        TrimIfNeeded();
        LogTextBox.Text = _text.ToString();
    }

    private void RebuildAll()
    {
        _text.Clear();
        if (DataContext is MainViewModel viewModel)
        {
            foreach (var line in viewModel.LogLines)
            {
                _text.AppendLine(line);
            }
        }

        TrimIfNeeded();
        LogTextBox.Text = _text.ToString();
    }

    private void TrimIfNeeded()
    {
        // Журнал за один запуск длинным не бывает, но ограничим на всякий случай.
        var lineCount = 0;
        for (var i = _text.Length - 2; i >= 0; i--)
        {
            if (_text[i] != '\n')
            {
                continue;
            }

            lineCount++;
            if (lineCount > MaxLines)
            {
                _text.Remove(0, i + 1);
                return;
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
                ScrollToEnd();
            }));
    }

    private void ScrollToEnd()
    {
        // Исключение вёрстки из прокрутки лога не должно улетать в глобальный обработчик:
        // прокрутка некритична, а падение здесь из CollectionChanged запускает каскад
        // MessageBox из вложенного pump диспетчера.
        try
        {
            LogTextBox.ScrollToEnd();
        }
        catch
        {
            // Подавляем: журнал продолжит наполняться без автопрокрутки.
        }
    }
}
