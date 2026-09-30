using System.Windows;
using System.Windows.Controls;
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
    private MainViewModel? _main;
    private bool _attached;

    public UserScriptsStrip()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Подписки на MainViewModel живут только пока контрол в дереве: при применении
        // меню view выбрасывается из кэша разделов, и без Unloaded-отписки контрол
        // оставался бы подписанным на UserScriptsChanged навсегда.
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (e.NewValue is MainViewModel newMain)
        {
            _main = newMain;
            _viewModel = new UserScriptsStripViewModel(newMain);
            DataContext = _viewModel;
            if (IsLoaded)
            {
                Attach();
            }

            Refresh();
        }
    }

    private void Attach()
    {
        if (_main is { } main && !_attached)
        {
            main.UserScriptsChanged += Refresh;
            _viewModel?.Attach();
            _attached = true;
        }
    }

    private void Detach()
    {
        if (_main is { } main && _attached)
        {
            main.UserScriptsChanged -= Refresh;
            _viewModel?.Detach();
            _attached = false;
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
            _viewModel?.Run(card);
        }
    }
}
