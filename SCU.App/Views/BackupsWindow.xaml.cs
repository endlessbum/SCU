using System.Windows;
using System.Windows.Interop;
using SCU.Common;
using SCU.Interop;

namespace SCU.Views;

// Окно «Бэкапы»: все созданные бэкапы приложения — раздел, время создания
// и назначение каждого. Данные собирает BackupCatalog из %AppData%\SCU\backup.
// Оформление строки заголовка — единое с «Редактированием меню» и главным окном
// (п. 8 аудита/задач): Mica-подложка, цвет заголовка = фон приложения,
// разворачивание строго в рабочую область (WorkAreaMaximize).
public partial class BackupsWindow : Window
{
    private bool _micaApplied;

    public BackupsWindow()
    {
        InitializeComponent();
        // Появление окна: fade + подъём корня (единый паттерн ConfirmDialog/OnboardingWindow).
        Loaded += (_, _) => SCU.Common.UiAnimations.AppearWindow(this);
        DataContext = AppCore.BackupCatalog.Collect();
        ThemeManager.ThemeApplied += OnThemeApplied;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => ThemeManager.ThemeApplied -= OnThemeApplied;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Единое правило заголовка: без выхода за пределы рабочей области при
        // максимизации — строка заголовка одной высоты во всех состояниях окна.
        WorkAreaMaximize.Attach(this);

        var isDark = ThemeManager.IsDarkTheme(ThemeManager.LoadThemeMode());
        _micaApplied = WindowEffects.TryApplyMica(this, isDark);
        if (!_micaApplied)
        {
            Background = (System.Windows.Media.Brush)FindResource("SolidRootBrush");
        }

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
