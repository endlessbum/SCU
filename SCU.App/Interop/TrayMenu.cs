using SCU.Common;

namespace SCU.Interop;

// Команды меню иконки в области уведомлений. Меню строится в MainWindow
// (WPF ContextMenu в стиле приложения), значения в Win32 не уходят, но
// фиксированы — на них опираются тесты модели.
internal enum TrayMenuCommand
{
    Clean = 1,
    Scanner = 2,
    Startup = 3,
    Settings = 4,
    Exit = 5,
}

// Готовый пункт меню: подпись уже переведена, доступность учитывает состояние.
// WithProgress — справа в пункте показывается процент выполнения (привязка
// к CleanupViewModel в MainWindow), по завершении операции проценты скрываются.
internal readonly record struct TrayMenuEntry(TrayMenuCommand Command, string Title, bool Enabled, bool WithProgress = false);

// Модель меню трея. Подписи берутся из словаря в момент открытия меню, поэтому
// смена языка применяется без пересборки иконки.
internal static class TrayMenu
{
    // isCleanupBusy — идёт «Очистить всё»: пункт «Очистить» неактивен, но
    // проценты продолжают обновляться, пока операция не завершится.
    internal static IReadOnlyList<TrayMenuEntry> Build(bool isCleanupBusy)
    {
        return
        [
            new(TrayMenuCommand.Clean, L.T("Очистить"), Enabled: !isCleanupBusy, WithProgress: true),
            new(TrayMenuCommand.Scanner, L.T("Сканер"), Enabled: true),
            new(TrayMenuCommand.Startup, L.T("Автозагрузка"), Enabled: true),
            new(TrayMenuCommand.Settings, L.T("Настройки"), Enabled: true),
            new(TrayMenuCommand.Exit, L.T("Выход"), Enabled: true),
        ];
    }
}
