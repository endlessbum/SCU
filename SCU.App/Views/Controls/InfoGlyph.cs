using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SCU.Views.Controls;

// Серый значок ⓘ рядом с названием утилиты. По нажатию (мышью или Space/Enter —
// это ToggleButton) открывает всплывающую панель с контекстным описанием;
// закрывается повторным нажатием, Escape или кликом мимо значка. Текст берётся
// из InfoText (локализованный ресурс или биндинг); длинный текст прокручивается,
// ширина панели ограничена, но не фиксирована.
public sealed class InfoGlyph : ToggleButton
{
    public static readonly DependencyProperty InfoTextProperty = DependencyProperty.Register(
        nameof(InfoText), typeof(string), typeof(InfoGlyph), new PropertyMetadata(string.Empty, OnInfoTextChanged));

    private const double PopupMaxWidth = 380;
    private const double PopupMaxHeight = 320;

    private Popup? _popup;

    // Гашение Click, завершающего закрытие Popup кликом по самому значку:
    // без этого toggle после Closed снова ставит IsChecked=true и панель переоткрывается.
    private bool _suppressNextClick;

    public InfoGlyph()
    {
        Focusable = true;
        Checked += OnChecked;
        Unchecked += OnUnchecked;
        KeyDown += OnKeyDown;
        UpdateAutomationName();
    }

    public string InfoText
    {
        get => (string)GetValue(InfoTextProperty);
        set => SetValue(InfoTextProperty, value);
    }

    private static void OnInfoTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((InfoGlyph)sender).UpdateAutomationName();

    // Имя для экранного диктора: содержимое подсказки, а при пустом InfoText —
    // локализованный общий ключ (текст может быть присвоен разделом позже).
    // Биндинги InfoText на DynamicResource приходят уже переведёнными и
    // обновляются вместе со словарём строк.
    private void UpdateAutomationName() =>
        SetValue(AutomationProperties.NameProperty,
            string.IsNullOrWhiteSpace(InfoText)
                ? Application.Current?.TryFindResource("S_InfoGlyph_AutomationName") as string ?? string.Empty
                : InfoText);

    private void OnChecked(object sender, RoutedEventArgs e)
    {
        var popup = _popup ??= CreatePopup();
        popup.IsOpen = true;
    }

    private void OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }
    }

    // Escape закрывает панель, фокус остаётся на значке.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsChecked == true)
        {
            SetCurrentValue(IsCheckedProperty, false);
            e.Handled = true;
        }
    }

    private Popup CreatePopup()
    {
        var text = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding
        {
            Path = new PropertyPath(InfoTextProperty),
            Source = this
        });

        // Прокрутка для длинного текста: панель не выходит за разумную высоту,
        // полоса появляется только когда содержимое не влезает.
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = PopupMaxHeight,
            Content = text
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = PopupMaxWidth
        };
        border.SetResourceReference(Border.BackgroundProperty, "PopupBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeBrush");
        border.Child = scroll;

        var popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Focusable = false,
            Child = border
        };

        // Клик мимо значка закрывает Popup сам (StaysOpen=false); сверяем переключатель,
        // чтобы следующий клик снова открывал подсказку.
        popup.Closed += (_, _) =>
        {
            if (IsChecked == true)
            {
                SetCurrentValue(IsCheckedProperty, false);
                // Курсор ещё над значком: текущий клик дозвонится в OnClick после закрытия —
                // гасим его, иначе базовый toggle переоткроет панель мгновенно.
                if (IsMouseOver)
                {
                    _suppressNextClick = true;
                }
            }
        };

        return popup;
    }

    protected override void OnClick()
    {
        if (_suppressNextClick)
        {
            _suppressNextClick = false;
            return;
        }

        base.OnClick();
    }
}
