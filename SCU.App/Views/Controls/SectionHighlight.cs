namespace SCU.Views.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SCU.ViewModels;

// П. 13 аудита: подсветка элемента/пункта меню после перехода по ссылке
// (пунктирный контур-адорнер), вынесена из MainWindow.
internal sealed class SectionHighlight
{
    private readonly Window _owner;
    private readonly MainViewModel _viewModel;
    private readonly System.Windows.Controls.ListBox _sidebarList;
    private readonly ScrollViewer _contentScroll;

    // Корень полосы пользовательских скриптов: она живёт ВНЕ ContentScroll
    // (MainWindow, Row 3), поэтому поиск карточки скрипта по заголовку идёт
    // и по ней. null — полосы в окне нет.
    private readonly DependencyObject? _scriptsRoot;

    public SectionHighlight(Window owner, MainViewModel viewModel,
        System.Windows.Controls.ListBox sidebarList, ScrollViewer contentScroll,
        DependencyObject? scriptsRoot = null)
    {
        _owner = owner;
        _viewModel = viewModel;
        _sidebarList = sidebarList;
        _contentScroll = contentScroll;
        _scriptsRoot = scriptsRoot;
    }

    // ===================== Подсветка элемента после перехода по ссылке =====================

    // Контур, играющий сейчас (новая вспышка снимает предыдущую,
    // чтобы рамки не наслаивались при быстрых переходах).
    private Views.Controls.SearchOutlineAdorner? _outlineAdorner;

    public void OnSectionHighlightRequested(int sectionNumber, string? elementTitle) =>
        // Навигация меняет видимость вкладок биндингами; поиск элемента и контур —
        // по уже показанному и разложенному разделу, поэтому после Layout.
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FlashSectionOutline(elementTitle));

    // Пунктирный контур элемента, к которому привёл переход: по заголовку
    // из подсказки поиска находится строка в открывшемся разделе и подсвечивается
    // её карточка. Если строки нет (переход без конкретного элемента, например из
    // бэнчмарка) — подсвечивается пункт меню раздела.
    public void FlashSectionOutline(string? elementTitle)
    {
        if (!string.IsNullOrEmpty(elementTitle) && FindRowCardByText(elementTitle) is { } card)
        {
            // Прокрутка так, чтобы карточка с запасом на контур была видна ЦЕЛИКОМ:
            // BringIntoView выравнивает карточку по нижней границе области просмотра,
            // и у пользователя она могла остаться наполовину за окном терминала.
            // Повторный проход после рендера страхует от поздних layout-сдвигов
            // (ленивый refresh раздела переставляет карточки).
            EnsureCardVisible(card);
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                EnsureCardVisible(card);
                _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => StartOutline(card));
            });
            return;
        }

        FlashSidebarOutline();
    }

    // Контур выступает за границы карточки на полпикселя; столько пикселей
    // отводим сверху и снизу при прокрутке, чтобы рамка была видна целиком.
    private const double OutlineScrollMargin = 8;

    // Прокрутка, гарантирующая полную видимость карточки с отступом под контур:
    // позиция карточки считается в координатах контента ScrollViewer и
    // сопоставляется с текущим окном просмотра (учитывает и низ, и верх).
    // Карточка вне контента скроллера (карточка скрипта в полосе под разделом)
    // прокрутки не требует — она и так видна; координаты в чужом дереве
    // считать нельзя, TransformToVisual бросил бы исключение.
    private void EnsureCardVisible(FrameworkElement card)
    {
        if (_contentScroll.Content is not Visual contentVisual
            || !card.IsDescendantOf(contentVisual)
            || _contentScroll.ViewportHeight <= 0
            || card.ActualHeight <= 0)
        {
            return;
        }

        var rect = card.TransformToVisual(contentVisual)
            .TransformBounds(new Rect(new Point(0, 0), card.RenderSize));
        var offset = _contentScroll.VerticalOffset;
        var viewportBottom = offset + _contentScroll.ViewportHeight;
        var bottom = rect.Bottom + OutlineScrollMargin;
        var top = rect.Top - OutlineScrollMargin;

        if (bottom > viewportBottom)
        {
            _contentScroll.ScrollToVerticalOffset(bottom - _contentScroll.ViewportHeight);
        }
        else if (top < offset)
        {
            _contentScroll.ScrollToVerticalOffset(top);
        }
    }

    // Ищет в открытом разделе карточку, в которой лежит заголовок утилиты из
    // подсказки. Совпадение сначала точное; если раздел называет параметр иначе
    // («Отключить гибернацию» → «Гибернация»), ищем по основе слова. Из
    // кандидатов выбирается самый точный.
    private FrameworkElement? FindRowCardByText(string title)
    {
        if (_contentScroll.Content is not DependencyObject root)
        {
            return null;
        }

        TextBlock? best = null;
        var bestScore = -1;
        Walk(root);
        // Полоса скриптов — вне скроллера: ищем и в ней (совпадение по точности
        // сравнивается с лучшим кандидатом из раздела).
        if (_scriptsRoot is not null)
        {
            Walk(_scriptsRoot);
        }

        return best is null ? null : PickRowCard(best, title);

        void Walk(DependencyObject node)
        {
            if (node is TextBlock block
                && block.IsVisible
                && block.Visibility == Visibility.Visible)
            {
                var score = MatchScore(title, block.Text);
                if (score > bestScore)
                {
                    best = block;
                    bestScore = score;
                }
            }

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private const int MatchThreshold = 4;

    // 3 — точное совпадение; 1–2 — совпадение по основе слова (не менее 4 символов
    // в основе, без учёта падежных окончаний); 0 — не совпало.
    private static int MatchScore(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (string.Equals(title.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        var stem = Stem(title);
        if (stem.Length < MatchThreshold)
        {
            return 0;
        }

        var normalized = text.Trim().ToLowerInvariant();
        if (normalized.Contains(stem, StringComparison.Ordinal)
            || stem.StartsWith(normalized, StringComparison.Ordinal))
        {
            return 2;
        }

        return 0;
    }

    // Основа для поиска: глагол действия («Отключить», «Включить», «Очистить»…)
    // отбрасывается, из остатка берётся начало — падежные окончания не важны.
    private static string Stem(string title)
    {
        var words = title.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var content = words.FirstOrDefault(word
            => word is not ("отключить" or "включить" or "установить" or "очистить" or "сбросить"
                or "задать" or "применить" or "запустить" or "создать" or "удалить" or "восстановить"));
        if (content is null)
        {
            return string.Empty;
        }

        return content.Length <= 6 ? content : content[..6];
    }

    // От заголовка строки поднимается к ближайшей подходящей карточке.
    private static FrameworkElement PickRowCard(TextBlock block, string title)
    {
        FrameworkElement? candidate = null;
        DependencyObject current = block;
        while (current is not null)
        {
            if (current is Border border
                && border.ActualHeight >= 44
                && border.ActualHeight <= 420)
            {
                candidate = border;
                break;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        // Карточка не найдена (строка без своего Border) — подсвечиваем сам
        // контейнер строки; не нашли и его — сам заголовок.
        return candidate
            ?? (VisualTreeHelper.GetParent(block) as FrameworkElement)
            ?? block;
    }

    // Свечение пункта меню: заголовок раздела всегда виден в сайдбаре.
    private void FlashSidebarOutline()
    {
        var section = _viewModel.CurrentSection;
        if (section is null)
        {
            return;
        }

        _sidebarList.ScrollIntoView(section);
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_sidebarList.ItemContainerGenerator.ContainerFromItem(section) is ListBoxItem item)
            {
                StartOutline(item);
            }
        });
    }

    // Одноразовый бегущий контур: секунда мягкого появления (рамка уже движется),
    // две секунды удержания, полсекунды исчезновения; эффект не остаётся на
    // постоянной основе.
    private void StartOutline(UIElement target)
    {
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer)
        {
            return;
        }

        if (_outlineAdorner is { } previous)
        {
            if (AdornerLayer.GetAdornerLayer(previous.AdornedElement) is { } previousLayer)
            {
                previousLayer.Remove(previous);
            }
            _outlineAdorner = null;
        }

        var brush = _owner.TryFindResource("AccentFillBrush") as Brush ?? Brushes.DodgerBlue;
        var outline = new Views.Controls.SearchOutlineAdorner(target, brush);
        layer.Add(outline);
        _outlineAdorner = outline;

        // Бегущий пунктир: смещение штриха за один период узора (6+4). Смещение
        // растёт — штрихи бегут против часовой стрелки (проверено рендером).
        // Один цикл — 0.7 с: движение заметное, но мягкое.
        var march = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(700))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        outline.Outline.BeginAnimation(System.Windows.Shapes.Rectangle.StrokeDashOffsetProperty, march);

        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(3500) };
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3500)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        });
        flash.Completed += (_, _) =>
        {
            layer.Remove(outline);
            if (ReferenceEquals(_outlineAdorner, outline))
            {
                _outlineAdorner = null;
            }
        };
        outline.BeginAnimation(UIElement.OpacityProperty, flash);
    }
}
