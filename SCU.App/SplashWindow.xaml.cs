using System.Windows;
using System.Windows.Media.Animation;

namespace SCU;

// Стартовая заставка: показывается мгновенно (без анимации появления) до открытия
// главного окна, гаснет после того, как окно уже показано пользователю.
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    // Плавное исчезание за 0.6 секунды с последующим закрытием окна.
    public void CloseAfterFade()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.6))
        {
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
