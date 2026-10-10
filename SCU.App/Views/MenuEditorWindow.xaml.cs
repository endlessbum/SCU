using System.Windows;
using System.Windows.Interop;
using SCU.Common;
using SCU.Interop;
using SCU.ViewModels;

namespace SCU.Views;

// Окно «Редактирование меню» (открывается из карточки настроек). Настройки
// карточек применяются кнопкой «Применить» (MenuEditorViewModel), операции
// со списком утилит — сразу. Строка заголовка — цветом фона приложения
// (как у окна браузера/чата): без DWM-крючков она оставалась светлой в тёмной теме.
public partial class MenuEditorWindow : Window
{
    private bool _micaApplied;

    public MenuEditorWindow(MainViewModel main, SCU.Infrastructure.Logging.Logger logger)
    {
        InitializeComponent();
        // Появление окна: fade + подъём корня (единый паттерн ConfirmDialog/OnboardingWindow).
        Loaded += (_, _) => SCU.Common.UiAnimations.AppearWindow(this);
        Owner = Application.Current?.MainWindow;
        DataContext = new MenuEditorViewModel(main, logger);
        ThemeManager.ThemeApplied += OnThemeApplied;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => ThemeManager.ThemeApplied -= OnThemeApplied;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Единое правило заголовка (п. 8): разворачивание строго в рабочую область —
        // DWM-строка заголовка одной и той же высоты в обычном и максимизированном виде.
        WorkAreaMaximize.Attach(this);

        var isDark = ThemeManager.IsDarkTheme(ThemeManager.LoadThemeMode());
        _micaApplied = WindowEffects.TryApplyMica(this, isDark);
        if (!_micaApplied)
        {
            Background = (System.Windows.Media.Brush)FindResource("SolidRootBrush");
        }

        // Строка заголовка — цветом фона приложения, чтобы не выделялась.
        WindowEffects.ApplyCaptionColor(this, isDark);
    }

    private void OnThemeApplied(bool isDark)
    {
        WindowEffects.ApplyCaptionColor(this, isDark);
        if (!_micaApplied)
        {
            // Без Mica DynamicResource не обновляет Background при смене темы.
            Background = (System.Windows.Media.Brush)FindResource("SolidRootBrush");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
