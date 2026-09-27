using System.Windows;
using System.Windows.Controls;
using SCU.Common;
using SCU.ViewModels;
using SCU.ViewModels.Sections;

namespace SCU.Views.Controls;

// Полоса пользовательских скриптов открытого раздела. Раздел задаётся
// зависимым свойством SectionNumber (биндинг на CurrentSection.Number).
public partial class UserScriptsStrip : UserControl
{
    public static readonly DependencyProperty SectionNumberProperty = DependencyProperty.Register(
        nameof(SectionNumber), typeof(int), typeof(UserScriptsStrip),
        new PropertyMetadata(-1, OnSectionNumberChanged));

    private UserScriptsStripViewModel? _viewModel;

    public UserScriptsStrip()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldMain)
        {
            oldMain.UserScriptsChanged -= Refresh;
        }

        if (e.NewValue is MainViewModel newMain)
        {
            _viewModel = new UserScriptsStripViewModel(newMain);
            DataContext = _viewModel;
            newMain.UserScriptsChanged += Refresh;
            Refresh();
        }
    }

    public int SectionNumber
    {
        get => (int)GetValue(SectionNumberProperty);
        set => SetValue(SectionNumberProperty, value);
    }

    private static void OnSectionNumberChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((UserScriptsStrip)d).Refresh();

    private void Refresh()
    {
        if (DataContext is UserScriptsStripViewModel viewModel)
        {
            viewModel.Refresh(SectionNumber);
        }
    }

    private void OnRunCardClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UserScriptCardViewModel card)
        {
            _viewModel.Run(card);
        }
    }
}
