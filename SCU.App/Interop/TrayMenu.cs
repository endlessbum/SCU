using SCU.Common;

namespace SCU.Interop;

// Команды меню иконки в области уведомлений. Числовые значения уходят в Win32
// как идентификаторы пунктов (AppendMenu/TrackPopupMenu), поэтому они фиксированы.
internal enum TrayMenuCommand
{
    ShowWindow = 1,
    DisableTranslator = 2,
    Exit = 3,
}

// Готовый пункт меню: подпись уже переведена, доступность учитывает состояние.
internal readonly record struct TrayMenuEntry(TrayMenuCommand Command, string Title, bool Enabled);

// Модель меню трея. Иконка в области уведомлений — единственный штатный путь
// завершить SCU, когда закрытие окна уходит в фоновый режим (включён перевод
// раскладки, App.RunInBackgroundOnClose). Модель отделена от Win32-кода ради
// тестируемости: подписи берутся из словаря в момент открытия меню, поэтому
// смена языка применяется без пересборки иконки.
internal static class TrayMenu
{
    // isTranslatorBusy — идёт операция раздела «Ввод»: остановка хука недоступна.
    internal static IReadOnlyList<TrayMenuEntry> Build(bool isTranslatorRunning, bool isTranslatorBusy)
    {
        return
        [
            new(TrayMenuCommand.ShowWindow, L.T("Показать SCU"), Enabled: true),
            // Включение перевода раскладки — только из UI: оно требует явного
            // согласия пользователя (диалог карточки «Ввод»).
            new(TrayMenuCommand.DisableTranslator, L.T("Выключить переводчик"), isTranslatorRunning && !isTranslatorBusy),
            new(TrayMenuCommand.Exit, L.T("Выход"), Enabled: true),
        ];
    }
}
