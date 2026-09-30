using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using SCU.Common;
using SCU.ViewModels.Sections;
using SCU.Views.Controls;

namespace SCU.Views.Sections;

// Раздел 24 «DeepSeek»: карточки подключения и чата. Код-бихайнд отвечает за
// пунктирный контур поля ввода ключа (адорнер) и открытие ссылки на платформу.
public partial class DeepSeekView : UserControl
{
    // Тайминги красного контура: 1 c появление → 2 c бег + мигание → статичное горение.
    private static readonly Duration AppearDuration = new(TimeSpan.FromSeconds(1));
    private static readonly TimeSpan MarchPhase = TimeSpan.FromSeconds(2);
    private static readonly Duration MarchPeriod = new(TimeSpan.FromMilliseconds(700));

    private readonly IShellOpenService _shell = new ShellOpenService();
    private SearchOutlineAdorner? _outline;
    private DispatcherTimer? _marchStopTimer;

    private DeepSeekViewModel? ViewModel => DataContext as DeepSeekViewModel;

    public DeepSeekView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DeepSeekViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.OpenChatRequested -= OnOpenChatRequested;
            oldVm.OpenAiAssistantRequested -= OnOpenAiAssistantRequested;
        }

        if (e.NewValue is DeepSeekViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            newVm.OpenChatRequested += OnOpenChatRequested;
            newVm.OpenAiAssistantRequested += OnOpenAiAssistantRequested;
            ApplyOutlineState(newVm.KeyOutlineState);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeepSeekViewModel.KeyOutlineState) && ViewModel is { } viewModel)
        {
            ApplyOutlineState(viewModel.KeyOutlineState);
        }
    }

    // ===================== Пунктирный контур поля ввода =====================

    private void ApplyOutlineState(DeepSeekKeyOutlineState state)
    {
        StopMarchPhase();

        var layer = AdornerLayer.GetAdornerLayer(ApiKeyBox);
        if (layer is null)
        {
            return;
        }

        if (_outline is not null)
        {
            layer.Remove(_outline);
            _outline = null;
        }

        switch (state)
        {
            case DeepSeekKeyOutlineState.None:
                return;
            case DeepSeekKeyOutlineState.Valid:
                // Зелёный статичный пунктир: без движения и миганий.
                _outline = CreateOutline((Brush)FindResource("SuccessBrush"));
                layer.Add(_outline);
                return;
            case DeepSeekKeyOutlineState.Invalid:
                _outline = CreateOutline((Brush)FindResource("DangerFillBrush"));
                layer.Add(_outline);
                StartMarchPhase();
                return;
        }
    }

    // Контур поля ввода: тот же штрих 6/4, что у drop-зоны «Сканера», но со
    // скруглением под форму строки ввода (у адорнера по умолчанию радиус 14 —
    // на крупном поле он смотрится чрезмерно круглым).
    private SearchOutlineAdorner CreateOutline(Brush brush)
    {
        var outline = new SearchOutlineAdorner(ApiKeyBox, brush);
        outline.Outline.RadiusX = 10;
        outline.Outline.RadiusY = 10;
        return outline;
    }

    // Красный контур: 1 c плавное появление, затем 2 c штрихи бегут по периметру
    // и контур плавно мигает, после чего анимации останавливаются — контур
    // продолжает гореть постоянно, пока состояние не сменится.
    private void StartMarchPhase()
    {
        if (_outline is not { } outline)
        {
            return;
        }

        // Появление (0→1 за 1 c); фаза движения запускается из Completed —
        // две анимации одного свойства Opacity одновременно конфликтуют.
        var appear = new DoubleAnimation(0, 1, AppearDuration);
        appear.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_outline, outline))
            {
                return;
            }

            // 10 = длина периода штриха 6+4: штрихи бегут против часовой стрелки.
            var march = new DoubleAnimation(0, 10, MarchPeriod)
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            outline.Outline.BeginAnimation(System.Windows.Shapes.Rectangle.StrokeDashOffsetProperty, march);

            var blink = new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromMilliseconds(660)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            outline.Outline.BeginAnimation(UIElement.OpacityProperty, blink);

            // Фаза движения длится 2 c, дальше контур замирает и горит постоянно.
            _marchStopTimer = new DispatcherTimer { Interval = MarchPhase };
            _marchStopTimer.Tick += OnMarchStopTimerTick;
            _marchStopTimer.Start();
        };
        outline.Outline.BeginAnimation(UIElement.OpacityProperty, appear);
    }

    private void OnMarchStopTimerTick(object? sender, EventArgs e)
    {
        StopMarchPhase();
        if (_outline is not { } outline)
        {
            return;
        }

        // «Останавливается в движении»: штрихи замирают на текущей позиции,
        // мигание прекращается — контур горит постоянно.
        var offset = outline.Outline.StrokeDashOffset;
        outline.Outline.BeginAnimation(System.Windows.Shapes.Rectangle.StrokeDashOffsetProperty, null);
        outline.Outline.StrokeDashOffset = offset;
        outline.Outline.BeginAnimation(UIElement.OpacityProperty, null);
        outline.Outline.Opacity = 1;
    }

    // Отмена фазы движения (смена состояния или повторный вход).
    private void StopMarchPhase()
    {
        if (_marchStopTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnMarchStopTimerTick;
            _marchStopTimer = null;
        }
    }

    // ===================== Ссылка на платформу и окно чата =====================

    private void OnApiKeyLinkRequested(object sender, RequestNavigateEventArgs e)
    {
        _shell.OpenPath(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OnOpenChatRequested()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var window = new DeepSeekChatWindow { DataContext = viewModel.Chat, Owner = Window.GetWindow(this) };
        window.Show();
        window.Activate();
    }

    // Окно AI-помощника: отдельный режим того же раздела (п. 27 ТЗ) —
    // обычный чат не затрагивается.
    private void OnOpenAiAssistantRequested()
    {
        if (ViewModel is not { Assistant: { } assistant })
        {
            return;
        }

        var window = new ScuAiAssistantWindow { DataContext = assistant, Owner = Window.GetWindow(this) };
        window.Show();
        window.Activate();
    }
}
