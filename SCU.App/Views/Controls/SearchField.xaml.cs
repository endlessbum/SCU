using System.Windows;
using System.Windows.Controls;

namespace SCU.Views.Controls;

// Общее поле поиска (Главная + Приложения): визуал и поведение ввода — здесь,
// смысл запроса — у владельца через SearchText (на «Главной» ищет утилиты и
// параметры по всей программе, в «Приложениях» — только среди приложений).
public partial class SearchField : UserControl
{
    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText), typeof(string), typeof(SearchField),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchField), new PropertyMetadata(string.Empty));

    public SearchField()
    {
        InitializeComponent();
        // Курсор рисуется на пиксель раньше текста (нативный наезжает на глиф).
        Loaded += (_, _) => SearchCaretAdorner.Attach(Field);
    }

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }
}
