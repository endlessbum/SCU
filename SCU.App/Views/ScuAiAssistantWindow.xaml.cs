using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SCU.ViewModels.Sections;

namespace SCU.Views;

// Окно SCU AI Assistant: агентный чат с карточками (п. 19 ТЗ). Код-бихайнд —
// только поведение окна: Enter для отправки, автопрокрутка переписки вниз
// после каждого сообщения, закрытие останавливает текущий ход.
public partial class ScuAiAssistantWindow : Window
{
    private ScuAiAssistantViewModel? ViewModel => DataContext as ScuAiAssistantViewModel;

    public ScuAiAssistantWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ScuAiAssistantViewModel oldVm)
        {
            oldVm.Messages.CollectionChanged -= OnMessagesChanged;
        }

        if (e.NewValue is ScuAiAssistantViewModel newVm)
        {
            newVm.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    // Новое сообщение — переписка прокручивается вниз (как в обычном чате).
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScrollToBottom();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        var scroll = MessagesScroll;
        if (scroll is null || VisualTreeHelper.GetDescendantBounds(scroll).Height <= 0)
        {
            return;
        }

        scroll.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        // П. 25 ТЗ: закрытие окна останавливает текущую AI-операцию.
        ViewModel?.StopCommand.Execute(null);
        base.OnClosed(e);
    }

    // Enter — отправить, Shift+Enter — перенос строки.
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        if (ViewModel?.SendCommand.CanExecute(null) == true)
        {
            ViewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
