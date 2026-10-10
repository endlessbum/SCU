using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SCU.Common;

// Централизованные анимации интерфейса: единые длительности, easing и учёт
// тумблера «Анимации интерфейса» (ThemeManager.AnimationsEnabled — при выключении
// все хелперы завершаются мгновенно, интерфейс работает как раньше).
// Анимируются только Opacity и TranslateTransform: они композитируются на GPU
// и не конфликтуют с Mica-фоном окна (DWM-бэкдроп).
//
// Правило дизайн-системы «что отключает тумблер» (аудит UX):
// — отключает: декоративные и повторяющиеся анимации (переходы разделов,
//   появление окон/диалогов, едущая пилюля, batch ring, marching-контуры);
// — не отключает: функциональные progress-индикаторы (AccentSpinner) и
//   короткое fade-раскрытие popup'ов (PopupAnimation.Fade) — раскрытие
//   содержимого, а не украшение.
public static class UiAnimations
{
    // Ховер/мелкие реакции — быстрее, чем переходы между разделами.
    public const int HoverMs = 120;
    public const int TabMs = 180;
    public const int SectionMs = 220;
    public const int WindowMs = 200;

    // Шаг каскада при первом появлении карточек раздела.
    public const int StaggerStepMs = 40;

    public static bool Enabled => ThemeManager.AnimationsEnabled;

    // Управляет XAML-триггерными анимациями (стиль NavListItem): значение
    // приходит из ресурса «UiAnimationsOn», который SetAnimations обновляет
    // в App.Resources при переключении тумблера.
    public static readonly DependencyProperty TriggerAnimationsProperty = DependencyProperty.RegisterAttached(
        "TriggerAnimations", typeof(bool), typeof(UiAnimations), new PropertyMetadata(true));

    public static bool GetTriggerAnimations(DependencyObject obj) => (bool)obj.GetValue(TriggerAnimationsProperty);
    public static void SetTriggerAnimations(DependencyObject obj, bool value) => obj.SetValue(TriggerAnimationsProperty, value);

    // Плавное появление прозрачностью (busy-оверлеи, всплывающие панели).
    // Внимание: у DoubleAnimation нет конструктора (from, duration) — вариант
    // (double, Duration) задаёт TO. Поэтому From/To указываются явно, чтобы
    // анимация не «уехала в ноль», и после завершения свойство возвращается
    // к базовой единице.
    public static void FadeIn(UIElement element, int ms = TabMs)
    {
        if (element is null || !Enabled)
        {
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // Появление сдвигом: fade + подъём (или выезд сбоку при fromX ≠ 0).
    // Используется для смены разделов и появления контента.
    public static void SlideFadeIn(FrameworkElement element, double fromX = 0, double fromY = 14, int ms = SectionMs)
    {
        if (element is null || !Enabled)
        {
            return;
        }

        var translate = GetOrCreateTranslate(element);
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, duration) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, duration) { EasingFunction = ease });
    }

    // Затухание с колбэком по завершении: фактическое закрытие (вкладки браузера)
    // происходит после исчезновения элемента. При выключенных анимациях колбэк — сразу.
    public static void FadeOutThen(UIElement element, int ms, Action? completed = null)
    {
        if (element is null)
        {
            completed?.Invoke();
            return;
        }

        if (!Enabled)
        {
            element.Opacity = 0;
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        if (completed is not null)
        {
            // После анимации свойство вернулось бы к базовой единице — фиксируем ноль,
            // элемент доживает до фактического удаления в колбэке.
            animation.Completed += (_, _) =>
            {
                element.Opacity = 0;
                completed();
            };
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    // Каскадное появление детей панели (первое открытие раздела): шаг stepMs.
    // До старта собственной анимации ребёнок скрыт (базовая Opacity 0), после —
    // база восстанавливается в единицу, повторные показы идут без каскада.
    public static void StaggerIn(Panel container, int stepMs = StaggerStepMs, int ms = SectionMs)
    {
        if (container is null || !Enabled)
        {
            return;
        }

        var delay = 0;
        foreach (var child in container.Children.OfType<FrameworkElement>())
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            var translate = GetOrCreateTranslate(child);
            child.Opacity = 0;
            var opacityAnimation = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(delay)
            };
            var captured = child;
            opacityAnimation.Completed += (_, _) =>
            {
                captured.Opacity = 1;
                captured.BeginAnimation(UIElement.OpacityProperty, null);
            };
            child.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);

            var duration = new Duration(TimeSpan.FromMilliseconds(ms));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var yAnimation = new DoubleAnimation(14, 0, duration) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(delay) };
            translate.BeginAnimation(TranslateTransform.YProperty, yAnimation);

            delay += stepMs;
        }
    }

    // Стандартное появление окна (тот же паттерн, что ConfirmDialog/OnboardingWindow):
    // fade + подъём корневого контента. Вызывается после Show/ShowDialog — окно
    // уже в дереве, BeginAnimation до первого кадра рендера заметен не будет.
    public static void AppearWindow(Window window)
    {
        if (window?.Content is FrameworkElement root)
        {
            SlideFadeIn(root, fromX: 0, fromY: 16, ms: WindowMs);
        }
    }

    // Появление с масштабом (шапка новой вкладки браузера): элемент
    // «вырастает» из левого края, сопроводительный fade.
    public static void ScaleFadeIn(FrameworkElement element, double fromScale = 0.8, int ms = TabMs)
    {
        if (element is null || !Enabled)
        {
            return;
        }

        var scale = GetOrCreateTransform<ScaleTransform>(element);
        element.RenderTransformOrigin = new Point(0, 0.5);
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, duration) { EasingFunction = ease });
    }

    // Возвращает (или создаёт) TranslateTransform элемента, не ломая существующий:
    // TransformGroup дополняется, одиночный трансформ оборачивается в группу.
    private static TranslateTransform GetOrCreateTranslate(FrameworkElement element) =>
        GetOrCreateTransform<TranslateTransform>(element);

    private static T GetOrCreateTransform<T>(FrameworkElement element) where T : Transform, new()
    {
        if (element.RenderTransform is not Transform existingTransform)
        {
            var fresh = new T();
            element.RenderTransform = fresh;
            return fresh;
        }

        if (existingTransform is T standalone)
        {
            return standalone;
        }

        if (existingTransform is TransformGroup group)
        {
            foreach (var child in group.Children)
            {
                if (child is T existing)
                {
                    return existing;
                }
            }

            var added = new T();
            group.Children.Add(added);
            return added;
        }

        var wrapped = new TransformGroup();
        wrapped.Children.Add(existingTransform);
        var translate = new T();
        wrapped.Children.Add(translate);
        element.RenderTransform = wrapped;
        return translate;
    }
}
