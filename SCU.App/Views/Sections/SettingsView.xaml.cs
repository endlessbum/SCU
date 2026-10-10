using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

    // Карточка «Путь очистки загрузок»: выбор дополнительной папки. Повторное
    // нажатие «Добавить» заменяет ранее указанный путь на новый.
    private void OnPickDownloadsCleanupFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } main)
        {
            return;
        }

        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = SCU.Common.L.T("Выбор папки для очистки загрузок"),
            };
            if (dialog.ShowDialog() == true)
            {
                main.SetDownloadsCleanupPath(dialog.FolderName);
            }
        }
        catch (Exception exception)
        {
            main.Log.Warn("SETTINGS | pick downloads cleanup folder failed | " + exception.Message);
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // Панель палитры: появляется рядом с квадратиком актуального цвета — слева
    // от него, видимый зазор PaletteGapPx, без наложения на квадратик. По
    // вертикали центр палитры совпадает с центром карточки, в которой лежит
    // квадратик: середина высоты палитры = середина высоты карточки. По
    // горизонтали центр палитры не привязан к карточке — только к квадратику.
    private const double PaletteGapPx = 10.0;

    private CustomPopupPlacement[] PlaceAccentPalettePopup(Size popupSize, Size targetSize, Point offset) =>
        PlacePalettePopup(popupSize, AccentSwatch);

    private CustomPopupPlacement[] PlaceIconPalettePopup(Size popupSize, Size targetSize, Point offset) =>
        PlacePalettePopup(popupSize, IconSwatch);

    private CustomPopupPlacement[] PlacePalettePopup(Size popupSize, FrameworkElement swatch)
    {
        // Координаты отсчитываются от левого верхнего угла квадратика.
        // x < 0 — палитра левее. Видимый правый край = x + ширина - 8
        // (Margin="8" палитры уже сдвинут край), отсюда поправка -8 в зазоре.
        // Дополнительный сдвиг влево на полширины квадратика: палитра
        // выравнивается по центру карточки, а не по правому краю квадратика.
        var x = -(popupSize.Width + PaletteGapPx - 8) - swatch.ActualWidth / 2;

        // Середина высоты карточки, переведённая в координаты квадратика:
        // половина высоты карточки минус отступ верха квадратика от верха
        // карточки. Fallback — центр самого квадратика (карточка не найдена).
        var centerY = swatch.ActualHeight / 2;
        if (FindOwningCard(swatch) is { } card && card.ActualHeight > 0)
        {
            var swatchTop = swatch.TransformToVisual(card).Transform(new Point(0, 0));
            centerY = card.ActualHeight / 2 - swatchTop.Y;
        }

        // popupSize включает Margin="8" палитры: центр бокса совпадает с
        // центром видимой палитры, поэтому y = центр карточки - высота/2.
        // Итоговый сдвиг от центра карточки: вверх на 1/6 ширины квадратика
        // (подъём на 1/3 минус опускание на 1/6) плюс 2 px вверх.
        var y = centerY - popupSize.Height / 2 - swatch.ActualWidth / 3 + swatch.ActualWidth / 6 - 2;
        return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
    }

    // Карточка (SectionCard), в которой лежит квадратик: подъём по визуальному
    // дереву до первого Border с этим стилем.
    private FrameworkElement? FindOwningCard(DependencyObject start)
    {
        var cardStyle = (Style)FindResource("SectionCard");
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is Border border && ReferenceEquals(border.Style, cardStyle))
            {
                return border;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
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
