using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // Панель палитры выдвигается влево от квадратика: правый край панели — в 8 px
    // от кнопки (видимый зазор; Margin="8" панели уже входит в размер попапа),
    // вертикаль — по центру кнопки независимо от высоты палитры.
    private CustomPopupPlacement[] CenterPalettePopup(Size popupSize, Size targetSize, Point offset)
    {
        var x = -popupSize.Width;
        var y = (targetSize.Height - popupSize.Height) / 2;
        return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
    }

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

    // Окно всех созданных бэкапов: раздел, время и назначение каждого.
    private void OnBackupsClick(object sender, RoutedEventArgs e)
    {
        new SCU.Views.BackupsWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    // Повторный показ окна знакомства (при первом запуске оно открывается само).
    private void OnOnboardingClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            SCU.Views.OnboardingWindow.Show(main, onlyIfFirstRun: false);
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
