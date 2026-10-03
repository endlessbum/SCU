using System.Windows;
using System.Windows.Media.Animation;
using SCU.Common;

namespace SCU.Views;

// Окно знакомства при первом запуске: в каком порядке использовать утилиты
// и о главных возможностях приложения. Визуал — как у ConfirmDialog (п. ТЗ:
// «в стиле UAC»): безрамочное окно-карточка, фон главного окна размывается
// на время показа. Флаг показа — settings.json (ThemeManager).
public partial class OnboardingWindow : Window
{
    public sealed class OnboardingItem
    {
        public OnboardingItem(string number, string title, string text)
        {
            Number = number;
            Title = title;
            Text = text;
        }

        public string Number { get; }

        public string Title { get; }

        public string Text { get; }
    }

    public OnboardingWindow()
    {
        InitializeComponent();
        TitleText = L.T("Добро пожаловать в SCU");
        SubtitleText = L.T("Короткий обзор: с чего начать и что здесь есть. Открыть это окно снова можно в «Настройках».");
        CloseTooltip = L.T("Закрыть");
        StartButtonText = L.T("Начать работу");
        Items =
        [
            new("1", L.T("Осмотритесь: «Информация о системе»"),
                L.T("Сводка о процессоре, памяти, дисках и Windows. Данные свежие — обновляются кнопкой «Обновить».")),
            new("2", L.T("Освободите место: раздел «Очистка»"),
                L.T("Корзина, временные папки, кэши браузеров и обновлений. На «Главной» есть быстрый выключатель для регулярной очистки.")),
            new("3", L.T("Отключите слежку: «Приватность и телеметрия»"),
                L.T("Тумблеры телеметрии и служб сбора данных. Каждый — с описанием и подтверждением перед изменением.")),
            new("4", L.T("Тонкая настройка: «Сеть», «Интерфейс», «Ввод», «Питание»"),
                L.T("Точечные тумблеры с честным состоянием: приложение читает реальное значение из системы и показывает его.")),
            new("5", L.T("Проверьте результат: «Бэнчмарк»"),
                L.T("Индекс состояния ПК по 10 областям. Запустите до изменений и после — увидите, что улучшилось, в сравнении «До → После».")),
            new("6", L.T("Безопасность: «Сканер» и подтверждения"),
                L.T("Сканер проверяет файлы и автозагрузку по обновляемой базе. Рисковые действия спрашивают подтверждения — тумблер «UAC» в «Настройках».")),
            new("★", L.T("Закрепляйте нужное на «Главной»"),
                L.T("Скрепка на карточке утилиты создаёт её дубликат на «Главной» — любимые настройки всегда под рукой.")),
            new("★", L.T("Свои скрипты: «Настройки → Добавить свой скрипт»"),
                L.T("Добавьте .ps1 или .bat — скрипт появится в полосе своего раздела, его можно закрепить и запустить в один клик.")),
            new("★", L.T("Настройте меню под себя"),
                L.T("«Редактирование меню» в «Настройках»: скрывайте разделы, создавайте свои вкладки, перетаскивайте утилиты.")),
            new("★", L.T("Поиск найдёт всё"),
                L.T("Поле поиска на «Главной» ищет по всем утилитам и параметрам, в «Приложениях» — по установленным программам.")),
            new("★", L.T("История операций"),
                L.T("Раздел «История» помнит, что и когда делалось; поддерживающие операции откатываются из записи журнала.")),
        ];
        DataContext = this;
        Loaded += (_, _) => ((Storyboard)Resources["AppearStoryboard"]).Begin(this);
    }

    public string TitleText { get; }

    public string SubtitleText { get; }

    public string CloseTooltip { get; }

    public string StartButtonText { get; }

    public IReadOnlyList<OnboardingItem> Items { get; }

    // Показ при первом запуске: фон главного окна размывается, после закрытия
    // ставится флаг в settings.json. Вызывается из MainWindow.OnLoaded и из
    // карточки «Знакомство с приложением» в «Настройках» (onlyIfFirstRun: false).
    public static void ShowOnce(MainWindow owner) => Show(owner, onlyIfFirstRun: true);

    public static void Show(MainWindow owner, bool onlyIfFirstRun)
    {
        if (onlyIfFirstRun && ThemeManager.LoadOnboardingShown())
        {
            return;
        }

        var window = new OnboardingWindow { Owner = owner };
        owner.SetContentBlur(true);
        try
        {
            window.ShowDialog();
        }
        finally
        {
            owner.SetContentBlur(false);
            ThemeManager.MarkOnboardingShown();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
