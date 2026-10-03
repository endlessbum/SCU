using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SCU.Common;

namespace SCU;

// Стартовая заставка: показывается мгновенно (без анимации появления), держит
// индикатор загрузки данных вкладок и гаснет через 2 секунды после того, как
// индикатор дошёл до 100% и главное окно уже показано.
public partial class SplashWindow : Window
{
    private const double TrackWidth = 320;

    public SplashWindow()
    {
        InitializeComponent();

        // Цвет заставки = цвет настройки «Цвет иконки приложения» (та же
        // перекраска пикселей, что у значка окна и иконки на рабочем столе).
        // Настройка читается до создания заставки (App.OnStartup), здесь только
        // применяем; подписка — на случай смены цвета, пока заставка видна.
        ApplyAccentColor(ThemeManager.CurrentIconAccentColor);
        ThemeManager.IconAccentChanged += OnIconAccentChanged;
        Closed += (_, _) => ThemeManager.IconAccentChanged -= OnIconAccentChanged;
    }

    private void OnIconAccentChanged() => ApplyAccentColor(ThemeManager.CurrentIconAccentColor);

    private void ApplyAccentColor(Color color)
    {
        Logo.Source = AccentIconManager.GetTitleBarIcon(color);

        var fill = new SolidColorBrush(color);
        fill.Freeze();
        ProgressFill.Fill = fill;

        var track = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B));
        track.Freeze();
        ProgressTrack.Background = track;
    }

    // Обновление индикатора: 0% — левый край логотипа, 100% — правый; без цифр.
    public void SetProgress(double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        ProgressFill.Width = TrackWidth * clamped / 100.0;
    }

    // Данные собраны: индикатор пропадает (логотип остаётся ещё на 2 секунды —
    // см. CloseAfterFade).
    public void HideProgressIndicator()
    {
        ProgressTrack.Visibility = Visibility.Collapsed;
    }

    // Плавное исчезание за 0.6 секунды; delay — пауза до начала гашения
    // (п. 4 запроса: логотип пропадает через 2 секунды после индикатора).
    // Topmost снимается и клики пропускаются: главное окно поднимается выше
    // и остаётся интерактивным, пока заставка ещё видна.
    public void CloseAfterFade(TimeSpan? delay = null)
    {
        Topmost = false;
        IsHitTestVisible = false;

        async void Run()
        {
            if (delay is { } pause)
            {
                await Task.Delay(pause).ConfigureAwait(true);
            }

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.6))
            {
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        }

        Run();
    }
}
