namespace SCU.Common;

// П. 13 аудита: чистые данные переводов, вынесены из фасада L (Localization.cs).
// Внутренняя деталь фасада: вызывать напрямую нельзя — только через L.T/L.S.
internal static partial class LocalizationDictionaries
{

    // === En ===

    // Шаблоны форматирования статуса, п. 13 аудита: чистые данные по зонам.
    internal static readonly Dictionary<string, string> StatusTemplates = new()
    {
        ["код {0}"] = "code {0}",
        ["Неизвестный компонент: {0}"] = "Unknown component: {0}",
        ["{0}: состояние не подтвердилось чтением."] = "{0}: state was not verified by reading.",
        ["WMI SystemRestore вернул код {0}."] = "WMI SystemRestore returned code {0}.",
        ["Удалено сертификатов: {0}."] = "Removed certificates: {0}.",
        ["Параметр не применился: фактическое значение {0}."] =
            "Parameter was not applied: actual value {0}.",
        ["Бэкап {0}\\{1}: не удалось прочитать значение."] =
            "Backup {0}\\{1}: failed to read the value.",
        ["Возвращено к заводским значениям ({0})."] =
            "Restored to factory values ({0}).",
        ["UAC: состояние не подтвердилось чтением (фактически {0})."] =
            "UAC: state was not verified by reading (actual {0}).",
        ["Активная схема не совпала: фактически {0}, ожидалось {1}."] =
            "Active plan did not match: actual {0}, expected {1}.",
        ["Неизвестная категория: {0}"] = "Unknown category: {0}",
        ["{0}: состояние не подтвердилось чтением (фактически {1})."] =
            "{0}: state was not verified by reading (actual {1}).",
        ["Файл слишком мал: {0} байт (ожидалось не меньше {1}) — вероятно, скачалась страница ошибки."] =
            "File is too small: {0} bytes (expected at least {1}) — an error page may have been downloaded.",
        ["Проверка подписи не пройдена: {0}"] = "Signature verification failed: {0}",
        ["Издатель не Microsoft: {0}"] = "Publisher is not Microsoft: {0}",
        ["Ошибка Authenticode 0x{0} ({1})."] = "Authenticode error 0x{0} ({1}).",
        ["Очистка: {0}…"] = "Cleaning: {0}…",
        ["{0}: уже пусто"] = "{0}: already empty",
        ["{0}: удалено файлов {1}"] = "{0}: deleted {1} files",
        ["{0}: удалено {1}, осталось {2} (занятые пропущены)"] =
            "{0}: deleted {1}, {2} remaining (busy files skipped)",
        ["Проверка подписи {0}…"] = "Verifying signature {0}…",
        ["Служба {0}: не удалось установить автозапуск."] =
            "Service {0}: failed to set automatic start.",
        ["Служба {0}: автозапуск отключён."] = "Service {0}: automatic start disabled.",
        ["Служба {0}: автозапуск включён."] = "Service {0}: automatic start enabled.",
        ["Служба {0}: включена."] = "Service {0}: enabled.",
        ["Служба {0}: отключена."] = "Service {0}: disabled.",
        ["Служба {0}: не удалось изменить состояние."] =
            "Service {0}: failed to change state.",
        ["Задержка меню: {0} мс."] = "Menu delay: {0} ms.",
        ["{0}: пакет отсутствует после проверки."] = "{0}: package is absent after verification.",
        ["{0}: удаление не подтверждено повторным сканированием (код {1})."] =
            "{0}: removal was not confirmed by the follow-up scan (code {1}).",
        ["Служба {0}: не удалось задать автозапуск."] = "Service {0}: failed to set automatic start.",
        ["{0}: автозапуск восстановлен через реестр (отложенный)."] =
            "{0}: automatic start restored through the registry (delayed).",
        ["{0}: тип запуска восстановлен, но служба не запущена ({1})."] =
            "{0}: start type restored, but the service is not running ({1}).",
        ["{0}: автозапуск восстановлен."] = "{0}: automatic start restored.",
        ["Служба {0}: не удалось задать тип запуска."] = "Service {0}: failed to set the start type.",
        ["Служба {0}: тип запуска не применился (Start={1}, ожидалось {2})."] =
            "Service {0}: start type was not applied (Start={1}, expected {2}).",
        ["{0}: включена ({1})."] = "{0}: enabled ({1}).",
        ["Служба {0} не найдена — пропущена."] = "Service {0} was not found — skipped.",
        ["{0}: уже остановлена или не удалось остановить ({1})."] =
            "{0}: already stopped or could not be stopped ({1}).",
        ["{0}: остановлена."] = "{0}: stopped.",
        ["Служба {0} отсутствует."] = "Service {0} is missing.",
        ["{0}: не удалось остановить ({1}). Возможно, службу держат зависимые службы или она защищена системой."] =
            "{0}: could not be stopped ({1}). A dependent service may be holding it, or it may be protected by the system.",
        ["{0}: не остановилась за 30 секунд — зависимые службы держат её. Попробуйте позже."] =
            "{0}: did not stop within 30 seconds — dependent services are holding it. Try again later.",
        ["Служба {0}: не остановилась."] = "Service {0}: did not stop.",
        ["Служба {0}: нет доступа — запустите SCU от имени администратора."] =
            "Service {0}: access denied — run SCU as administrator.",
        ["Служба {0}: не удалось получить доступ к службе."] =
            "Service {0}: could not access the service.",
        ["Служба {0} {1}. Было: Start={2}, State={3}."] =
            "Service {0} {1}. Previous: Start={2}, State={3}.",
        ["NetBIOS mode {0} применён ко всем активным интерфейсам."] =
            "NetBIOS mode {0} applied to all active interfaces.",
        ["TCP Global восстановлены (Auto-Tuning={0}, ECN={1})."] =
            "TCP Global restored (Auto-Tuning={0}, ECN={1}).",
        ["MTU для {0} не применился: фактически {1}, ожидалось {2}."] =
            "MTU for {0} was not applied: actual {1}, expected {2}.",
        ["MTU {0} установлен для {1} и подтверждён чтением."] =
            "MTU {0} set for {1} and verified by reading.",
        ["QoS override = {0}%."] = "QoS override = {0}%.",
        ["Не удалось прочитать текущий MTU: {0}"] = "Failed to read the current MTU: {0}",
        ["Не удалось записать бэкап MTU: {0}"] = "Failed to save the MTU backup: {0}",
        ["Не удалось сохранить/установить QoS — профиль отменён: {0}"] =
            "Failed to save/apply QoS — profile cancelled: {0}",
        ["Профиль применён частично: {0}"] = "Profile applied partially: {0}",
        ["Сброс завершён не полностью: {0}"] = "Reset did not complete fully: {0}",
        ["Не удалось сохранить бэкап свойств адаптеров: {0}"] = "Failed to save the adapter properties backup: {0}",
        ["Свойства адаптеров восстановлены из бэкапа ({0} значений)."] = "Adapter properties restored from the backup ({0} values).",
        ["Свойства адаптеров восстановлены не полностью: значений {0}, ошибок {1}, ошибок перезапуска {2}; бэкап сохранён."] =
            "Adapter properties were not fully restored: {0} values, {1} errors, {2} restart errors; the backup is preserved.",
        ["Не удалось восстановить свойства адаптеров: {0}"] = "Failed to restore the adapter properties: {0}",
        ["Не удалось записать бэкап TCP Global: {0}"] = "Failed to save the TCP Global backup: {0}",
        ["Не удалось изменить QoS: {0}"] = "Failed to change QoS: {0}",
        ["Не удалось сохранить исходный QoS: {0}"] = "Failed to save the original QoS: {0}",
        ["Интерфейс «{0}» не найден."] = "Interface «{0}» was not found.",
        ["Файл подкачки: {0} МБ. Вступит в силу после перезагрузки."] =
            "Page file: {0} MB. Takes effect after reboot.",
        ["Размер pagefile {0} не подтвердился после записи."] =
            "Page file size {0} was not verified after writing.",
        ["Не удалось настроить файл подкачки: {0}"] = "Failed to configure the page file: {0}",
        ["Не удалось прочитать BCD: {0}"] = "Failed to read the BCD: {0}",
        ["Не удалось повторно проверить BCD: {0}"] = "Failed to verify the BCD again: {0}",
        ["Не удалось переключить сжатие памяти (код {0})."] =
            "Failed to toggle memory compression (code {0}).",
        ["Не удалось распознать состояние: «{0}»."] = "Could not recognize the state: «{0}».",
        ["Не удалось прочитать список планов: {0}"] = "Failed to read the power plan list: {0}",
        ["Не удалось создать копию плана: {0}"] = "Failed to duplicate the power plan: {0}",
        ["План активирован, но не удалось подтвердить его чтением: {0}"] =
            "The power plan was activated, but could not be verified by reading: {0}",
        ["Активная схема не совпала: фактически {0}, ожидалось {1}."] =
            "The active plan did not match: actual {0}, expected {1}.",
        ["powercfg завершился с кодом {0}"] = "powercfg exited with code {0}",
        ["Не удалось открыть раздел реестра для резервирования файла подкачки."] =
            "Failed to open the registry key for backing up the page file settings.",
        ["Не удалось сохранить бэкап файла подкачки: {0}"] =
            "Failed to save the page file backup: {0}",
        ["Не удалось записать {0}\\{1}: {2}"] = "Failed to write {0}\\{1}: {2}",
        ["Не удалось удалить {0}: значение осталось в реестре."] =
            "Failed to delete {0}: the value remains in the registry.",
        ["Не удалось удалить {0}: {1}"] = "Failed to delete {0}: {1}",
        ["Не удалось открыть {0} на запись."] = "Failed to open {0} for writing.",
        ["Служба {0}: {1}"] = "Service {0}: {1}",
        ["{0}: не удалось задать автозапуск."] = "{0}: failed to set automatic start.",
        ["Загрузчик"] = "Boot loader",
        ["Скачано {0} bytes."] = "Downloaded {0} bytes.",
        ["{0} установлен."] = "{0} installed.",
        ["{0} уже установлен."] = "{0} is already installed.",
        ["{0} установлен, требуется перезагрузка."] = "{0} installed; reboot required.",
        ["{0}: установщик завершился с кодом {1}"] = "{0}: installer exited with code {1}",
        ["{0}: файл подозрительно мал ({1} bytes)"] = "{0}: file is suspiciously small ({1} bytes)",
        ["Скачивание {0} не удалось: {1}"] = "Download of {0} failed: {1}",
        ["{0}: состояние не подтвердилось чтением (фактически {1})."] =
            "{0}: state was not verified by reading (actual {1}).",
        ["{0}: выключена ({1})."] = "{0}: disabled ({1}).",
        ["Установка «{0}»…"] = "Installing «{0}»…",
        ["Удаление {0}"] = "Removing {0}",
        ["Отключение службы {0}…"] = "Disabling service {0}…",
        ["Включение службы {0}…"] = "Enabling service {0}…",
        ["Не удалось изменить {0}: {1}"] = "Failed to change {0}: {1}",
        ["Не удалось установить «{0}»: {1}"] = "Failed to install «{0}»: {1}",
        ["Не удалось отключить «{0}» (код {1}): {2}"] =
            "Failed to disable «{0}» (code {1}): {2}",
        ["Откат завершился с кодом {0}: {1}"] = "Rollback finished with code {0}: {1}",
        ["Не удалось прочитать автозагрузку (код {0}): {1}"] =
            "Failed to read startup entries (code {0}): {1}",
        ["Загружено служб: {0}. {1}. Бэкап: {2}."] =
            "Loaded services: {0}. {1}. Backup: {2}.",
        ["Загружено элементов: {0}. {1}."] = "Loaded entries: {0}. {1}.",
        ["Откат выполнен. {0}"] = "Restore completed. {0}",
        ["{0} Список не обновлён: {1}"] = "{0} List was not refreshed: {1}",
        ["Операция отменена. Часть служб могла остаться отключённой — используйте «Вернуть обновления»."] =
            "Operation cancelled. Some services may remain disabled — use «Unblock updates».",
        ["Не все службы обновления отключены: {0}"] =
            "Not all update services were disabled: {0}",
        ["Дампы и WER: {0}"] = "Dumps and WER: {0}",
        ["{0}: удалено {1} / {2}"] = "{0}: deleted {1} / {2}",
        ["{0} Список обновлён ({1})."] = "{0} List refreshed ({1}).",
        ["Не удалось прочитать задачи: {0}"] = "Failed to read tasks: {0}",
        ["Не удалось прочитать службы: {0}"] = "Failed to read services: {0}",
        ["Бэкап сохранён: {0}"] = "Backup saved: {0}",
        ["Элемент отключён. {0}"] = "Entry disabled. {0}",
        ["Задача отключена. {0}"] = "Task disabled. {0}",
        ["Отключение задач"] = "Disabling tasks",
        ["Отключить все перечисленные задачи ({0})?\nВернуть прежние состояния можно через «Откатить»."] =
            "Disable all listed tasks ({0})?\nPrevious states can be restored via «Restore».",
        ["Отключение всех задач…"] = "Disabling all tasks…",
        ["Задачи отключены. {0}"] = "Tasks disabled. {0}",
        ["Не удалось отключить задачи (код {0}): {1}"] =
            "Failed to disable tasks (code {0}): {1}",
        ["Отключение всех задач ({0})"] = "All tasks disabled ({0})",
        ["Загружено задач: {0}."] =
            "Loaded tasks: {0}.",
        ["Бэкап не сохранён (код {0}): {1}"] = "Backup not saved (code {0}): {1}",
        ["Ошибка: {0}"] = "Error: {0}",
        ["Ошибка (код {0}): {1}"] = "Error (code {0}): {1}",
    };


    // === StatusFragments ===
}
