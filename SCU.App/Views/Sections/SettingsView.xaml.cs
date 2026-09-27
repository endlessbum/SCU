using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SCU.Models;
using SCU.ViewModels;

namespace SCU.Views.Sections;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldMain)
        {
            oldMain.ScriptPlacementRequested -= OnScriptPlacementRequested;
            oldMain.InstalledScriptsRequested -= OnInstalledScriptsRequested;
        }

        if (e.NewValue is MainViewModel newMain)
        {
            newMain.ScriptPlacementRequested += OnScriptPlacementRequested;
            newMain.InstalledScriptsRequested += OnInstalledScriptsRequested;
        }
    }

    // Скрипт прошёл проверку — мастер размещения карточки.
    private void OnScriptPlacementRequested(string sourcePath)
    {
        if (ViewModel is { } main)
        {
            new SCU.Views.ScriptWizardWindow(main, sourcePath) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
    }

    private void OnInstalledScriptsRequested()
    {
        if (ViewModel is { } main)
        {
            new SCU.Views.InstalledScriptsWindow(main, main.Log) { Owner = Window.GetWindow(this) }.ShowDialog();
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // Квадратик-тумблер: popup закрывается по mousedown вне его (StaysOpen=False)
    // раньше Click — состояние ДО закрытия фиксируем в PreviewMouseDown, иначе
    // повторный клик переоткрывал бы палитру вместо закрытия.
    private bool _accentWasOpen;
    private bool _iconWasOpen;

    private void OnOpenMenuEditorClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } main)
        {
            new SCU.Views.MenuEditorWindow(main, main.Log).Show();
        }
    }

    private void OnAccentSwatchPreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        _accentWasOpen = AccentPopup.IsOpen;

    private void OnAccentSwatchClick(object sender, RoutedEventArgs e) =>
        AccentPopup.IsOpen = !_accentWasOpen;

    private void OnAccentChoiceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AccentChoice choice)
        {
            ViewModel?.ApplyAccent(choice.Mode);
        }

        AccentPopup.IsOpen = false;
    }

    private void OnIconSwatchPreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        _iconWasOpen = IconPopup.IsOpen;

    private void OnIconSwatchClick(object sender, RoutedEventArgs e) =>
        IconPopup.IsOpen = !_iconWasOpen;

    private void OnIconChoiceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AccentChoice choice)
        {
            ViewModel?.ApplyIconAccent(choice.Mode);
        }

        IconPopup.IsOpen = false;
    }
}
