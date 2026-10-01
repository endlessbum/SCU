namespace SCU.Common;

// П. 13 аудита: чистые данные переводов, вынесены из фасада L (Localization.cs).
// Внутренняя деталь фасада: вызывать напрямую нельзя — только через L.T/L.S.
internal static partial class LocalizationDictionaries
{

    // === En ===

    // Переводы интерфейса (ru -> en), п. 13 аудита: чистые данные по зонам.
    internal static readonly Dictionary<string, string> En = new()
    {
        // ===================== Статусы (общие) =====================
        ["Операция отменена."] = "Operation cancelled.",
        ["Операция отменена. Состояние служб восстановлено."] = "Operation cancelled. Service state restored.",
        ["Состояние обновлено."] = "State refreshed.",
        ["Скан AppX ещё не выполнялся."] = "The AppX scan has not been run yet.",
        ["Показано текущее состояние категорий."] = "The current category state is shown.",
        ["показ…"] = "showing…",
        ["скрытие…"] = "hiding…",
        ["Корзина очищена; занятые элементы пропущены."] = "Recycle Bin cleaned; busy items were skipped.",
        ["State has not been read yet"] = "State has not been read yet",
        ["Состояние категорий обновлено."] = "Category state refreshed.",
        ["Состояние обновлено. Изменения проводника применяются после перезапуска."] = "State refreshed. Explorer changes apply after Explorer restarts.",
        ["Загрузка состояния…"] = "Loading state…",
        ["Нужны права администратора — изменение параметров ввода и игр недоступно."] = "Administrator rights are required — input and gaming settings are unavailable.",
        ["Создание точки восстановления… (до минуты)"] = "Creating a restore point… (up to a minute)",
        ["DISM /AnalyzeComponentStore… (до нескольких минут)"] = "DISM /AnalyzeComponentStore… (may take several minutes)",
        ["DISM /StartComponentCleanup… (вывод — в журнале ниже)"] = "DISM /StartComponentCleanup… (output is shown in the log below)",
        ["Не удалось прочитать список точек: {0}"] = "Failed to read the list of restore points: {0}",
        ["Удаление точки"] = "Delete restore point",
        ["Удалить точку восстановления из системного хранилища?\n\n{0}\n\nТочка перестанет отображаться в списке и станет недоступна для восстановления. Продолжить?"] = "Delete the restore point from the system store?\n\n{0}\n\nThe point will disappear from the list and will no longer be available for restore. Continue?",
        ["Удаление точки восстановления…"] = "Deleting the restore point…",
        ["Точка восстановления удалена."] = "Restore point deleted.",
        ["Нет прав на удаление точки — запустите SCU от имени администратора."] =
            "No rights to delete the point — run SCU as administrator.",
        ["Не удалось удалить точку восстановления (код {0})."] = "Failed to delete the restore point (code {0}).",
        ["Не удалось удалить точку восстановления: {0}"] = "Failed to delete the restore point: {0}",
        ["Очистка выполнена. {0}"] = "Cleanup completed. {0}",
        ["Очистка выполнена. Повторный анализ не удался: {0}"] = "Cleanup completed. Re-analysis failed: {0}",
        ["Очистка не выполнена."] = "Cleanup was not performed.",
        ["Очистка не выполнена (код {0}): {1}"] = "Cleanup failed (code {0}): {1}",
        ["Нечего очищать: целевые папки не найдены."] = "Nothing to clean: target folders were not found.",
        ["Удалено файлов: {0} ({1})."] = "Files deleted: {0} ({1}).",
        [" Осталось занятых: {0}."] = " Still occupied: {0}.",
        ["байт"] = "bytes",
        ["Очистка кэша Delivery Optimization…"] = "Cleaning the Delivery Optimization cache…",
        ["Очистка дампов памяти и очереди отчётов об ошибках…"] = "Cleaning memory dumps and the error-report queue…",
        ["Восстановить"] = "Restore",
        ["Чтение параметров интерфейса…"] = "Reading interface settings…",
        ["Ослабление UAC"] = "Weakening UAC",
        ["Установить PromptOnSecureDesktop=0 и ConsentPromptBehaviorAdmin=0?\nЭто снижает защиту от повышения прав и считается опасной операцией.\nТекущие значения будут сохранены в бэкап. Применяется после перезагрузки."] = "Set PromptOnSecureDesktop=0 and ConsentPromptBehaviorAdmin=0?\nThis reduces elevation protection and is considered a dangerous operation.\nCurrent values will be saved to a backup. Applies after reboot.",
        ["Чтение параметров UAC…"] = "Reading UAC settings…",
        ["Загружено элементов: {0}. {1}."] = "Loaded entries: {0}. {1}.",
        ["SCU.ps1 недоступен."] = "SCU.ps1 is unavailable.",
        ["SCU.ps1 недоступен, бэкап не создан."] = "SCU.ps1 is unavailable; no backup was created.",
        ["Корзина очищена; отдельные системные или занятые элементы пропущены."] = "Recycle Bin cleaned; some system or busy items were skipped.",
        ["Корзина не очищена (код {0}): {1}"] = "Recycle Bin was not cleaned (code {0}): {1}",
        ["Выберите операцию очистки."] = "Choose a cleanup operation.",
        ["Список компонентов не загружен"] = "Components list has not been loaded",
        ["Список компонентов не загружен."] = "The components list has not been loaded yet.",
        // ===================== Большой выключатель «Состояния ПК» =====================
        ["Нужны права администратора — применение пакета недоступно."] = "Administrator rights are required — the batch cannot be applied.",
        ["Список пуст — включите утилиты в настройках ниже."] = "The list is empty — enable utilities in the settings below.",
        ["Применение: {0} ({1} из {2})…"] = "Applying: {0} ({1} of {2})…",
        ["Пропущено: раздел занят, нет прав или утилита неприменима."] = "Skipped: the section is busy, rights are missing, or the utility cannot be applied.",
        ["Готово: выполнено {0} из {1}."] = "Done: {0} of {1} applied.",
        ["Пакет отменён: выполнено {0} из {1}."] = "Batch cancelled: {0} of {1} applied.",
        ["Оптимизация"] = "Optimization",
        ["Пакетное применение ({0} утилит)"] = "Batch apply ({0} utilities)",
        ["Включено: {0}, выполнено: {1}."] = "Included: {0}, applied: {1}.",
        ["Выбрано: {0} из {1} утилит"] = "Selected: {0} of {1} utilities",
        ["К применению: {0}"] = "Ready to apply: {0}",
        ["Права администратора"] = "Administrator rights",
        ["Ошибка приложения"] = "Application error",
        ["Ошибка фоновой операции"] = "Background operation failed",
        ["SCU уже запущен"] = "SCU is already running",
        ["Одновременно может работать только одна копия приложения."] =
            "Only one copy of the application can run at a time.",
        ["Фоновая запись и DVR"] = "Background recording and DVR",
        ["Общий поиск…"] = "Universal search…",
        // ===================== Бэнчмарк (раздел 20) =====================
        ["Проверка системы…"] = "Scanning the system…",
        ["Проверка завершена."] = "Check completed.",
        ["Ещё не проверялось — запустите первую проверку."] = "Not checked yet — run the first check.",
        ["Проверено: {0} из {1} · Покрытие: {2}%"] = "Verified: {0} of {1} · Coverage: {2}%",
        ["Не удалось прочитать: {0}"] = "Failed to read: {0}",
        ["Не удалось удалить: {0}"] = "Failed to remove: {0}",
        ["не удалось измерить"] = "measurement failed",
        // ===== Раздел 25 «Драйверы» =====
        ["Состояние драйверов ещё не проверено."] = "Driver status has not been checked yet.",
        ["Нужны права администратора — установка и экспорт драйверов недоступны."] = "Administrator rights are required — driver installation and export are unavailable.",
        ["Чтение устройств и драйверов…"] = "Reading devices and drivers…",
        ["Список драйверов обновлён. Для поиска свежих версий используйте «Проверить обновления»."] = "Driver list refreshed. Use «Check for updates» to find newer versions.",
        ["Устройств: {0} · Проблемных: {1} · Обновлений: {2}"] = "Devices: {0} · Problem: {1} · Updates: {2}",
        ["Подключение к службе обновлений…"] = "Connecting to the update service…",
        ["Поиск обновлений драйверов на серверах Windows Update…"] = "Searching for driver updates on the Windows Update servers…",
        ["Превышено время ожидания поиска обновлений ({0} мин) — серверы Windows Update недоступны."] = "The update search timed out ({0} min) — Windows Update servers are unreachable.",
        ["Не удалось выполнить поиск обновлений: {0}"] = "The update search failed: {0}",
        ["Обновлений драйверов не найдено — все установлены."] = "No driver updates found — everything is installed.",
        ["Найдено обновлений: {0}"] = "Updates found: {0}",
        ["Поиск завершён. Установите нужные пакеты кнопкой «Обновить»."] = "Search finished. Install the packages you need with «Update».",
        ["Поиск завершён."] = "Search finished.",
        ["Поиск недоступен: службы обновления заблокированы. Используйте «Вернуть обновления» в разделе «Обновления Windows»."] = "Search is unavailable: update services are blocked. Use «Restore updates» in the Windows Updates section.",
        ["Службы обновления заблокированы (раздел «Обновления Windows») — поиск недоступен, пока они отключены."] = "Update services are blocked (Windows Updates section) — search is unavailable while they are disabled.",
        ["Авто-установка драйверов через Windows Update запрещена; поиск по запросу продолжает работать."] = "Automatic driver installation via Windows Update is disabled; on-demand search still works.",
        ["Обновление всех драйверов"] = "Update all drivers",
        ["Будут загружены и установлены все найденные пакеты: {0}.\nПродолжить?"] = "All found packages will be downloaded and installed: {0}.\nContinue?",
        ["Подготовка к установке…"] = "Preparing installation…",
        ["Загрузка пакетов драйверов…"] = "Downloading driver packages…",
        ["Установка драйверов…"] = "Installing drivers…",
        ["Обновления не найдены — выполните поиск заново."] = "Updates not found — run the search again.",
        ["Загрузка пакетов не завершилась (код {0})."] = "Package download did not finish (code {0}).",
        ["Не удалось установить драйвер: {0}"] = "Failed to install the driver: {0}",
        ["Установка требует прав администратора."] = "Installation requires administrator rights.",
        ["Серверы Windows Update недоступны (таймаут сети)."] = "Windows Update servers are unreachable (network timeout).",
        ["Не удалось связаться с серверами Windows Update — проверьте сеть или настройки прокси."] = "Could not reach the Windows Update servers — check the network or proxy settings.",
        ["Без названия"] = "Untitled",
        ["Создание точки восстановления…"] = "Creating a restore point…",
        ["Обновление драйверов SCU"] = "SCU driver update",
        ["Не удалось создать точку восстановления: {0}\nПродолжить установку без неё?"] = "Could not create a restore point: {0}\nContinue without it?",
        ["Продолжить"] = "Continue",
        ["Установка отменена."] = "Installation canceled.",
        ["Установлено пакетов: {0}.{1}"] = "Packages installed: {0}.{1}",
        ["Требуется перезагрузка."] = "A restart is required.",
        ["Установлено: {0}, не удалось: {1}. {2}"] = "Installed: {0}, failed: {1}. {2}",
        ["Драйверы"] = "Drivers",
        ["Обновление драйверов ({0})"] = "Driver update ({0})",
        ["Драйверы обновлены"] = "Drivers updated",
        ["Драйверы: частичная ошибка"] = "Drivers: partial failure",
        ["Запрет авто-драйверов через Windows Update…"] = "Disabling automatic drivers via Windows Update…",
        ["Папка для экспорта драйверов"] = "Folder for driver export",
        ["Экспорт пакетов драйверов…"] = "Exporting driver packages…",
        ["Не удалось создать папку: {0}"] = "Could not create the folder: {0}",
        ["pnputil завершился с ошибкой (код {0}): {1}"] = "pnputil failed (code {0}): {1}",
        ["Экспортировано пакетов: {0} → {1}"] = "Packages exported: {0} → {1}",
        ["Экспорт драйверов"] = "Driver export",
        ["Не выбрана папка экспорта."] = "No export folder selected.",
        ["драйвер не установлен"] = "driver not installed",
        ["код {0}"] = "code {0}",
        ["Отмена операции…"] = "Cancelling the operation…",
        ["COM-компонент Windows Update (Microsoft.Update.Session) недоступен на этой системе."] = "The Windows Update COM component (Microsoft.Update.Session) is unavailable on this system.",
        ["Подтверждено"] = "Confirmed",
        ["Изменилось к худшему"] = "Changed for the worse",
        ["Доступно +{0} баллов"] = "+{0} points available",
        ["Критическая ошибка"] = "Critical error",
        ["Не удалось запустить приложение"] = "Failed to start the application",
        ["Подробности в журнале (файл лога SCU)."] = "Details are in the log (SCU log file).",
        ["Скрипт не запущен"] = "Script was not started",
        ["Скан не запущен"] = "Scan was not started",
        ["Без изменений"] = "No change",
        ["Не удалось проверить"] = "Could not verify",
        ["Не применимо"] = "Not applicable",
        ["Требует перезапуска"] = "Requires restart",
        ["Версия Windows определена"] = "Windows version identified",
        ["Свободно на системном диске"] = "Free space on the system drive",
        ["Службы доступны для управления"] = "Services manageable",
        ["Задачи доступны для управления"] = "Tasks manageable",
        ["Схема питания прочитана"] = "Power plan read",
        ["UAC — стандартный уровень"] = "UAC — standard level",
        ["Настройки приватности применены"] = "Privacy settings applied",
        ["Состояние обновлений прочитано"] = "Update state read",
        ["Сетевые параметры прочитаны"] = "Network settings read",
        ["Диск и очистка"] = "Disk and cleanup",
        ["Исходный индекс серии: {0}"] = "Series baseline index: {0}",
        ["Свободное место на диске с Windows. Очистка временных файлов и кэшей увеличивает показатель."] =
            "Free space on the Windows drive. Cleaning temp files and caches increases it.",
        ["Элементы, запускаемые вместе с Windows. Управляются в разделе «Автозагрузка»."] =
            "Entries launched together with Windows. Managed in the «Startup» section.",
        ["Службы контрольного набора читаются без ошибок — их состояние можно менять и проверять."] =
            "Control-set services read without errors — their state can be changed and verified.",
        ["Задачи планировщика контрольного набора прочитаны."] =
            "Control-set scheduled tasks were read.",
        ["Активная схема питания определена; управляется в разделе «Питание»."] =
            "The active power plan is identified; managed in the «Power» section.",
        ["Контроль учётных записей на стандартном уровне. Ослабленный UAC — отклонение универсальной политики."] =
            "User Account Control at the standard level. Weakened UAC deviates from the universal policy.",
        ["Доля применённых категорий приватности. Управляется в разделе «Приватность»."] =
            "Share of applied privacy categories. Managed in the «Privacy» section.",
        ["Состояние службы обновлений определено (блокировка/пауза — осознанные настройки)."] =
            "The update service state is identified (block/pause are deliberate settings).",
        ["Сетевые параметры прочитаны; управляются в разделе «Сеть»."] =
            "Network settings were read; managed in the «Network» section.",
        ["Базовые сведения о системе прочитаны."] = "Basic system information was read.",
        ["прочитано"] = "read",
        ["Поиск среди приложений…"] = "Search apps…",
        ["Удалить «{0}» полностью?\n\nБудут принудительно завершены процессы приложения, запущен штатный деинсталлятор без вопросов, затем удалены оставшиеся папки приложения (папка установки, AppData, ProgramData).\nДействие необратимо. Продолжить?"] =
            "Uninstall «{0}» completely?\n\nApp processes will be force-closed, the native uninstaller will run silently, then leftover app folders (install folder, AppData, ProgramData) will be removed.\nThis cannot be undone. Continue?",
        ["Принудительное удаление «{0}»…"] = "Force-uninstalling «{0}»…",
        ["Скан завершён. Удаление необратимо: восстановление возможно только переустановкой из Store/winget."] = "Scan completed. Removal is irreversible; recovery requires reinstalling from Store/winget.",
        ["За раз проверяется один объект — обработан только первый."] = "One item is scanned at a time — only the first one was processed.",
        ["Полный список AppX не прочитан — удаление заблокировано, статусы не считаются подтверждёнными."] = "The full AppX list could not be read — removal is blocked and package states are not treated as verified.",
        ["Корзина очищена."] = "Recycle Bin cleaned.",
        ["Часть служб не удалось вернуть — см. лог."] = "Some services could not be restored — see the log.",
        ["Состояние служб восстановлено."] = "Service state restored.",
        ["Шаг 1 из 2: DISM /RestoreHealth… (вывод — в журнале ниже)"] = "Step 1 of 2: DISM /RestoreHealth… (output is shown in the log below)",
        ["Анализ завершён."] = "Analysis completed.",
        ["Анализ не удался — см. карточку."] = "Analysis failed — see the card for details.",
        ["Дампы и WER: "] = "Dumps and WER: ",
        ["Сжатие системных файлов (compact /always)…"] = "Compressing system files (compact /always)…",
        ["Распаковка системных файлов…"] = "Decompressing system files…",
        ["MTU {0} вне диапазона 576..1500."] = "MTU {0} is outside the 576..1500 range.",
        ["Удаление QoS override…"] = "Removing the QoS override…",
        ["Установка QoS override = {0}%…"] = "Setting QoS override = {0}%…",
        ["Сброс завершён не полностью: {0}"] = "Reset did not complete fully: {0}",
        ["Установка плана: {0}…"] = "Setting the power plan: {0}…",
        ["План «{0}» установлен."] = "Power plan «{0}» is set.",
        ["Включение гибернации…"] = "Enabling hibernation…",
        ["Отключение гибернации…"] = "Disabling hibernation…",
        ["Включение быстрого запуска…"] = "Enabling Fast Startup…",
        ["Отключение быстрого запуска…"] = "Disabling Fast Startup…",
        ["Включение сжатия памяти…"] = "Enabling memory compression…",
        ["Отключение сжатия памяти…"] = "Disabling memory compression…",
        ["Включение SysMain…"] = "Enabling SysMain…",
        ["Отключение SysMain…"] = "Disabling SysMain…",
        ["Включение имён 8.3…"] = "Enabling 8.3 short names…",
        ["Отключение имён 8.3…"] = "Disabling 8.3 short names…",
        ["Включение учёта времени доступа…"] = "Enabling last-access timestamp updates…",
        ["Отключение учёта времени доступа…"] = "Disabling last-access timestamp updates…",
        ["Включение prefetcher…"] = "Enabling the prefetcher…",
        ["Отключение prefetcher…"] = "Disabling the prefetcher…",
        ["Восстановление задач CEIP из бэкапа…"] = "Restoring CEIP tasks from the backup…",
        ["Отключение задач CEIP (через SCU.ps1)…"] = "Disabling CEIP tasks (via SCU.ps1)…",
        ["Бэкап задач не создавался — включение недоступно."] = "No task backup was created — enabling is unavailable.",
        ["Ошибка восстановления (код {0}): {1}. Частично можно исправить через раздел «Службы Windows»."] = "Restore failed (code {0}): {1}. Some changes can be fixed from the «Windows Services» section.",
        ["UAC на стандартном уровне Windows."] = "UAC is at the standard Windows level.",
        ["UAC ослаблен (подтверждения выведены с безопасного рабочего стола)."] = "UAC is weakened (confirmations are shown without the secure desktop).",
        ["Перезапуск проводника…"] = "Restarting Explorer…",
        ["Очистка панели задач…"] = "Cleaning the taskbar…",
        ["Восстановление панели задач…"] = "Restoring the taskbar…",
        ["Список автозагрузки ещё не загружен."] = "The startup list has not been loaded yet.",
        ["Элемент отключён. {0}"] = "Entry disabled. {0}",
        ["Откат выполнен. {0}"] = "Rollback completed. {0}",
        ["Загружено задач: {0}."] = "Loaded tasks: {0}.",
        ["Отключение задачи {0}…"] = "Disabling task {0}…",
        ["Задача отключена. {0}"] = "Task disabled. {0}",
        ["Включение службы {0}…"] = "Enabling service {0}…",
        ["Включение стандартного уровня UAC…"] = "Enabling the standard UAC level…",
        ["Ослабление UAC…"] = "Weakening UAC…",
        ["Не удалось прочитать службы: {0}"] = "Failed to read services: {0}",
        ["Можно удалить"] = "Safe to remove",
        ["Системное приложение"] = "System application",
        ["Системный компонент. Удалять не рекомендуется: могут перестать работать зависящие от него программы и компоненты. При необходимости его можно установить заново."] = "System component. Removal is not recommended: dependent programs and components may stop working. It can be reinstalled if needed.",

        // ===================== Подсказки о правах =====================
        ["Нужны права администратора — операции раздела недоступны."] = "Administrator rights are required — section operations are unavailable.",
        ["Нужны права администратора — удаление недоступно."] = "Administrator rights are required — removal is unavailable.",
        ["Нужны права администратора — операции очистки недоступны."] = "Administrator rights are required — cleanup operations are unavailable.",
        ["Нужны права администратора — управление службами недоступно."] = "Administrator rights are required — service management is unavailable.",
        ["Нужны права администратора — изменение параметров приватности недоступно."] = "Administrator rights are required — privacy settings are unavailable.",
        ["Нужны права администратора — изменение параметров питания недоступно."] = "Administrator rights are required — power settings are unavailable.",
        ["Нужны права администратора — изменение сетевых параметров недоступно."] = "Administrator rights are required — network settings are unavailable.",
        ["Нужны права администратора — изменение параметров интерфейса недоступно."] = "Administrator rights are required — interface settings are unavailable.",
        ["Нужны права администратора — изменение параметров ввода недоступно."] = "Administrator rights are required — input settings are unavailable.",
        ["Нужны права администратора — управление обновлениями недоступно."] = "Administrator rights are required — update management is unavailable.",
        ["Нужны права администратора — изменение параметров UAC недоступно."] = "Administrator rights are required — UAC settings are unavailable.",
        ["Нужны права администратора. Список доступен только для чтения."] = "Administrator rights are required. The list is read-only.",
        ["SCU.ps1 недоступен: очистка корзины и кэша обновлений невозможна."] = "SCU.ps1 is unavailable: the Recycle Bin and update cache cannot be cleaned.",
        ["SCU.ps1 недоступен — скан и удаление заблокированы."] = "SCU.ps1 is unavailable — scan and removal are blocked.",
        ["SCU.ps1 недоступен, изменение автозагрузки невозможно."] = "SCU.ps1 is unavailable — startup cannot be changed.",
        ["SCU.ps1 недоступен, изменение задач невозможно."] = "SCU.ps1 is unavailable — tasks cannot be changed.",
        ["SCU.ps1 недоступен, изменение служб невозможно."] = "SCU.ps1 is unavailable — services cannot be changed.",

        // ===================== Очистка =====================
        ["Очистка корзины"] = "Recycle bin cleanup",
        ["Безвозвратно очистить корзину на всех дисках?"] = "Permanently empty the Recycle Bin on all drives?",
        ["Очистка корзины…"] = "Cleaning the Recycle Bin…",
        ["Очистка папок Temp"] = "Temp folders cleanup",
        ["Удалить временные файлы (TEMP, Windows\\Temp, Prefetch, Recent, INetCache)?\nЗанятые файлы будут пропущены."] = "Delete temporary files (TEMP, Windows\\Temp, Prefetch, Recent, INetCache)?\nBusy files will be skipped.",
        ["Очистка временных файлов…"] = "Cleaning temporary files…",
        ["Очистка кэша браузеров"] = "Browser cache cleanup",
        ["Удалить кэш браузеров (Chrome, Edge, Яндекс, Brave, Vivaldi, Firefox, Opera)?\nЗакладки и пароли не затрагиваются. Закройте браузеры, иначе часть кэша будет занята."] = "Delete browser caches (Chrome, Edge, Yandex, Brave, Vivaldi, Firefox, Opera)?\nBookmarks and passwords are not affected. Close the browsers, otherwise some cache files will be locked.",
        ["Очистка кэша браузеров…"] = "Cleaning browser caches…",
        ["Очистка кэша обновлений Windows"] = "Windows Update cache cleanup",
        ["Будут остановлены службы обновления (wuauserv, bits, cryptsvc, msiserver) с сохранением состояния,\nзатем очищен кэш SoftwareDistribution\\Download и Delivery Optimization.\nСостояние служб будет восстановлено автоматически."] = "The update services (wuauserv, bits, cryptsvc, msiserver) will be stopped with their state saved,\nthen the SoftwareDistribution\\Download and Delivery Optimization caches will be cleaned.\nThe services state will be restored automatically.",
        ["Сохранение состояния служб обновления…"] = "Saving the state of update services…",
        ["Остановка служб обновления…"] = "Stopping update services…",
        ["Восстановление исходного состояния служб…"] = "Restoring the original state of services…",
        ["Очистка отменена: не удалось сохранить состояние служб (код {0})."] = "Cleanup cancelled: failed to save the service state (code {0}).",

        // ===================== Мусорное ПО =====================
        ["Сканирование фактически установленных AppX…"] = "Scanning installed AppX packages…",
        ["Массовое удаление AppX и Edge… Это может занять несколько минут."] = "Mass removal of AppX and Edge… This may take several minutes.",
        ["Удаление "] = "Removing ",
        ["Удаление — {0}"] = "Removal — {0}",
        ["Удалить «{0}»?\nПакеты: {1}{2}"] = "Remove «{0}»?\nPackages: {1}{2}",
        ["Удалить всё перечисленное"] = "Remove everything listed",
        ["Будут удалены ВСЕ перечисленные AppX, Microsoft Store и Microsoft Edge.\n\nОперация НЕОБРАТИМА: Store и Edge могут потребоваться другим приложениям,\nа Edge-удаление может затронуть WebView2.\n\nПродолжить?"] = "ALL listed AppX packages, Microsoft Store and Microsoft Edge will be removed.\n\nThe operation is IRREVERSIBLE: other apps may need Store and Edge,\nand Edge removal may affect WebView2.\n\nContinue?",
        ["Удалить"] = "Remove",
        ["Не удалось изменить {0}: {1}"] = "Failed to change {0}: {1}",
        ["Не удалось отключить {0} (код {1}): {2}"] = "Failed to disable {0} (code {1}): {2}",
        ["пакеты удалены (проверено скриптом)"] = "packages removed (verified by the script)",
        ["неизвестно — выполните скан"] = "unknown — run the scan",

        // ===================== Компоненты =====================
        ["Определение установленных компонентов…"] = "Detecting installed components…",
        ["Загружено компонентов: {0}."] = "Loaded components: {0}.",
        ["Установка «{0}»…"] = "Installing «{0}»…",
        ["Установить"] = "Set",
        ["Не удалось установить «{0}»: {1}"] = "Failed to install «{0}»: {1}",
        ["Удаление компонента"] = "Component removal",
        ["Удалить «{0}»?\nБудет запущен официальный установщик в тихом режиме (/uninstall /quiet)."] =
            "Uninstall «{0}»?\nThe official installer will run in silent mode (/uninstall /quiet).",
        ["Не удалось удалить «{0}»: {1}"] = "Failed to uninstall «{0}»: {1}",
        ["Список программ ещё не загружен."] = "The apps list has not been loaded yet.",
        ["Чтение установленных программ…"] = "Reading installed programs…",
        ["Программ найдено: {0}."] = "Loaded programs: {0}.",
        ["Программ найдено: {0}"] = "Programs found: {0}",
        ["Удаление приложения"] = "App removal",
        ["Удалить «{0}»?\nБудет запущен штатный деинсталлятор приложения."] =
            "Uninstall «{0}»?\nThe app's built-in uninstaller will be launched.",
        ["Запущено удаление «{0}». После завершения обновите список."] =
            "Uninstalling «{0}» has started. Refresh the list when it finishes.",
        ["Отключить подтверждения рисковых действий?\nОперации будут выполняться сразу, без окна подтверждения."] =
            "Disable risky action confirmations?\nOperations will run immediately, without a confirmation window.",

        // ===================== Приватность =====================
        ["Включение категорий приватности"] = "Enabling privacy categories",
        ["Все категории ниже будут выключены:"] =
            "All categories below will be disabled:",
        ["Включить все"] = "Enable all",
        ["Все категории включены."] = "All categories are enabled.",
        ["Включение категорий: часть операций не удалась — {0}"] = "Enabling categories: some operations failed — {0}",
        ["Включение: "] = "Enabling: ",
        ["Отключение — {0}"] = "Disabling — {0}",
        ["Отключить «{0}»?\n{1}\nТекущие значения реестра будут сохранены в бэкап."] = "Disable «{0}»?\n{1}\nThe current registry values will be saved to a backup.",
        ["Отключить"] = "Disable",
        ["Отключить: "] = "Disable: ",
        ["{0}: ошибка (код {1}): {2}"] = "{0}: error (code {1}): {2}",

        // ===================== Службы =====================
        ["Чтение статусов служб…"] = "Reading service states…",
        ["Сохранение бэкапа служб…"] = "Saving the services backup…",
        ["Восстановление служб из бэкапа…"] = "Restoring services from the backup…",
        ["Отключение службы {0}…"] = "Disabling service {0}…",
        ["Отключение всех служб"] = "Disabling all services",
        ["Будут отключены все службы из списка ({0}):\n{1}\n\nБэкап текущих состояний будет создан автоматически, «Откатить» вернёт всё как было. Продолжить?"] =
            "All services from the list will be disabled ({0}):\n{1}\n\nA backup of the current states will be created automatically; «Restore» will bring everything back. Continue?",
        ["Отключить все"] = "Disable all",
        ["Все службы отключены."] = "All services are disabled.",
        ["Отключение служб: часть операций не удалась — {0}"] = "Disabling services: some operations failed — {0}",
        ["Список служб ещё не загружен."] = "The services list has not been loaded yet.",
        ["Не удалось прочитать службы: "] = "Failed to read services: ",
        ["Загружено служб: {0}. {1}. Бэкап: {2}."] = "Loaded services: {0}. {1}. Backup: {2}.",
        ["Нет службы"] = "No service",
        ["Работает"] = "Running",
        ["Включить"] = "Enable",
        ["Включить: "] = "Enable: ",

        // ===== Состояния effective state (службы/питание, п.27 плана) =====
        ["Запускается…"] = "Starting…",
        ["Останавливается…"] = "Stopping…",
        ["Возобновляется…"] = "Resuming…",
        ["Приостанавливается…"] = "Pausing…",
        ["Приостановлена"] = "Paused",
        ["Неизвестно"] = "Unknown",
        ["Загрузка"] = "Boot",
        ["Автоматически"] = "Automatic",
        ["Автоматически (отложенный)"] = "Automatic (Delayed Start)",
        ["Вручную"] = "Manual",
        ["Служба отсутствует"] = "Service not found",
        ["Нет доступа"] = "Access denied",
        ["Таймаут"] = "Timeout",
        ["Ошибка запроса"] = "Query error",
        ["Состояние не удалось определить"] = "The state could not be determined",
        ["Запуск разрешён"] = "Start allowed",
        ["Запуск запрещён"] = "Start disallowed",
        ["Служба: {0}\nТип запуска: {1}\nСостояние: {2}\nОтложенный запуск: {3}"] = "Service: {0}\nStartup type: {1}\nStatus: {2}\nDelayed start: {3}",
        ["Служба: {0}\n{1}\nНе удалось получить достоверное состояние Windows.\nПроверьте права администратора или повторите проверку."] = "Service: {0}\n{1}\nA reliable Windows state could not be obtained.\nCheck administrator rights or run the check again.",
        ["Описание для этой службы отсутствует. Показаны только сведения, определённые Windows."] = "No description is available for this service. Only the information defined by Windows is shown.",
        ["SysMain: "] = "SysMain: ",
        ["Быстрый запуск: {0}"] = "Fast Startup: {0}",
        ["Сжатие памяти: состояние неизвестно ({0})."] = "Memory compression: state unknown ({0}).",
        ["Включение System Managed pagefile…"] = "Enabling the system-managed page file…",
        ["Режим: Управляется Windows"] = "Mode: Managed by Windows",
        ["Режим: Заданный вручную"] = "Mode: Custom",
        ["Управляется Windows"] = "Managed by Windows",
        ["Файл подкачки: состояние не удалось определить."] = "Page file: the state could not be determined.",
        ["Требуется перезагрузка для полного применения."] = "A reboot is required for the change to apply fully.",
        ["Установить фиксированный размер {0} МБ?\nТома: {1}\nАвтоматическое управление Windows будет отключено.\nИзменения вступят в силу после перезагрузки."] = "Set a fixed size of {0} MB?\nVolumes: {1}\nWindows automatic management will be disabled.\nThe change takes effect after a reboot.",
        ["Передать управление размером файла подкачки Windows (System Managed)?\nФиксированные размеры будут сняты. Изменение вступит в силу после перезагрузки."] = "Hand page file management back to Windows (system managed)?\nFixed sizes will be removed. The change takes effect after a reboot.",
        ["вкл"] = "on",
        ["выкл"] = "off",
        ["Общий режим: для всех томов"] = "Global mode: enabled for all volumes",
        ["Общий режим: отключено для всех"] = "Global mode: disabled for all volumes",
        ["Общий режим: настраивается для каждого тома"] = "Global mode: configured per volume",
        ["Общий режим: отключено кроме системного"] = "Global mode: disabled except the system volume",
        ["Общий режим: неизвестно"] = "Global mode: unknown",
        ["Восстановление из точки"] = "Restoring from a restore point",
        ["Запуск отката Windows SystemRestore…"] = "Starting the Windows System Restore rollback…",
        ["Применение универсального профиля к сетевым адаптерам…"] = "Applying the universal profile to network adapters…",
        ["Auto-Tuning: {0}, ECN: {1}, QoS: {2}"] = "Auto-Tuning: {0}, ECN: {1}, QoS: {2}",
        ["NetBIOS: {0}"] = "NetBIOS: {0}",
        ["{0}: {1}"] = "{0}: {1}",
        ["LSO v2"] = "LSO v2",

        // ===================== Автозагрузка =====================
        ["Чтение автозагрузки…"] = "Reading startup entries…",
        ["Восстановление автозагрузки…"] = "Restoring startup entries…",
        ["Отключение автозагрузки"] = "Disabling a startup entry",
        ["Отключить элемент «{0}» ({1})?\nКоманда: {2}"] = "Disable the entry «{0}» ({1})?\nCommand: {2}",
        ["Восстановление автозагрузки"] = "Restoring startup entries",
        ["Вернуть ранее отключённые элементы автозагрузки из манифеста SCU.ps1?"] = "Restore previously disabled startup entries from the SCU.ps1 manifest?",
        ["Элемент отключён. "] = "Entry disabled. ",
        ["Откат выполнен. "] = "Restore completed. ",
        ["Откат завершился с кодом {0}: {1}"] = "Restore finished with code {0}: {1}",
        ["Не удалось прочитать автозагрузку (код {0}): {1}"] = "Failed to read startup entries (code {0}): {1}",
        ["Не удалось отключить «{0}» (код {1}): {2}"] = "Failed to disable «{0}» (code {1}): {2}",
        ["Отключение «{0}»…"] = "Disabling «{0}»…",

        // ===================== Питание =====================
        ["Чтение состояния (powercfg, bcdedit, реестр)…"] = "Reading state (powercfg, bcdedit, registry)…",
        ["Установка плана: "] = "Setting the power plan: ",
        ["Файл подкачки"] = "Page file",
        ["Установить фиксированный файл подкачки C:\\pagefile.sys = {0} МБ?\nАвтоматическое управление будет отключено.\nИзменения вступят в силу после перезагрузки."] = "Set a fixed page file C:\\pagefile.sys = {0} MB?\nAutomatic management will be disabled.\nThe changes take effect after a reboot.",
        ["Настройка файла подкачки ({0} МБ)…"] = "Configuring the page file ({0} MB)…",
        ["Размер файла подкачки не распознан."] = "The page file size was not recognized.",
        ["Ограничения CPU и ОЗУ"] = "CPU and RAM limits",
        ["Удалить из BCD параметры numproc и truncatememory (если заданы)?\nПосле удаления Windows не будет ограничивать число CPU и объём ОЗУ через эти параметры.\nПрименяется после перезагрузки; изменения проверяются повторным чтением BCD."] = "Remove the numproc and truncatememory parameters from the BCD (if set)?\nAfterwards Windows will not limit the CPU count and RAM size via these parameters.\nApplied after a reboot; the changes are verified by re-reading the BCD.",
        ["Снятие ограничений numproc/truncatememory…"] = "Removing numproc/truncatememory limits…",
        ["Убрать ограничения"] = "Remove limits",

        // ===================== Сеть =====================
        ["Профиль сетевого адаптера"] = "Network adapter profile",
        ["Будет применён универсальный сбалансированный профиль к физическим сетевым адаптерам. Неподдерживаемые свойства будут пропущены.\nИзменения расширенных параметров могут кратковременно прервать сетевое соединение. Продолжить?"] = "A universal balanced profile will be applied to physical network adapters. Unsupported properties will be skipped.\nChanging advanced properties may briefly interrupt network connectivity. Continue?",
        ["Применить универсальный профиль к сетевым адаптерам…"] = "Applying the universal profile to network adapters…",
        ["Разгрузка контрольных сумм"] = "Checksum Offload",
        ["Модерация прерываний"] = "Interrupt Moderation",
        ["Управление потоком"] = "Flow Control",
        ["Jumbo Packet"] = "Jumbo Packet",
        ["Energy Efficient Ethernet"] = "Energy Efficient Ethernet",
        ["Частота модерации прерываний"] = "Interrupt Moderation Rate",
        ["{0}: {1} — применено"] = "{0}: {1} — applied",
        ["{0}: {1} — пропущено (нет свойства)"] = "{0}: {1} — skipped (property not supported)",
        ["{0}: {1} — ошибка"] = "{0}: {1} — error",
        ["Профиль: {0}"] = "Profile: {0}",
        ["Перезапуск изменённого сетевого адаптера…"] = "Restarting the changed network adapter…",
        ["Физические сетевые адаптеры не найдены."] = "No physical network adapters were found.",
        ["Профиль применён не полностью: {0}"] = "The profile was only partially applied: {0}",
        ["Обработано адаптеров: {0}; изменено: {1}; пропущено: {2}; ошибок настройки: {3}; ошибок проверки: {4}; ошибок чтения: {5}; ошибок перезапуска: {6}."] = "Adapters processed: {0}; changed: {1}; skipped: {2}; setting errors: {3}; verification errors: {4}; read errors: {5}; restart errors: {6}.",
        ["Универсальный профиль применён: адаптеров {0}; изменено {1}; пропущено {2} неподдерживаемых свойств."] = "Universal profile applied: {0} adapters; {1} changed; {2} unsupported properties skipped.",
        ["Чтение TCP Global, MTU, QoS и NetBIOS…"] = "Reading TCP Global, MTU, QoS and NetBIOS…",
        ["Выберите интерфейс в списке."] = "Select an interface in the list.",
        ["MTU не распознан — введите число 576..1500 или используйте пресеты."] = "MTU not recognized — enter a number 576..1500 or use the presets.",
        ["Восстановление сохранённых MTU…"] = "Restoring saved MTU values…",
        ["Восстановление исходного QoS override…"] = "Restoring the original QoS override…",
        ["Применение игрового профиля…"] = "Applying the gaming profile…",
        ["Откат игрового профиля из бэкапа…"] = "Restoring the gaming profile from the backup…",
        ["Игровой профиль отключён: {0}"] = "Gaming profile disabled: {0}",
        ["Восстановление свойств сетевых адаптеров из бэкапа…"] = "Restoring network adapter properties from the backup…",
        ["Цель MTU: {0}"] = "MTU target: {0}",
        ["Восстановление всех параметров из бэкапов…"] = "Restoring all settings from backups…",
        ["Профиль для игр"] = "Gaming profile",
        ["Будут применены: Auto-Tuning=disabled, ECN=disabled, QoS override=0%.\nПрофиль не гарантирует снижение пинга; исходные значения сохраняются в бэкап.\nПродолжить?"] = "The following will be applied: Auto-Tuning=disabled, ECN=disabled, QoS override=0%.\nThe profile does not guarantee lower ping; the original values are saved to a backup.\nContinue?",
        ["Применить профиль"] = "Apply profile",
        ["Сброс сетевых параметров"] = "Network settings reset",
        ["Восстановить TCP Global, QoS override, MTU и NetBIOS из сохранённых бэкапов?"] = "Restore TCP Global, QoS override, MTU and NetBIOS from the saved backups?",
        ["Восстановить всё"] = "Restore everything",
        ["Применить"] = "Apply",
        ["Исходные режимы сохраняются в бэкап."] = "The original modes are saved to a backup.",
        ["netsh: MTU {0} для «{1}»…"] = "netsh: MTU {0} for «{1}»…",
        ["netsh: {0}={1}…"] = "netsh: {0}={1}…",
        ["WMI SetTcpipNetbios mode={0}…"] = "WMI SetTcpipNetbios mode={0}…",
        ["ipconfig /flushdns…"] = "ipconfig /flushdns…",
        ["Сертификат Минцифры"] = "Mincifry certificate",
        ["Удалить сертификаты «Russian Trusted Root CA» из доверенных корневых и промежуточных центров сертификации?"] =
            "Remove the «Russian Trusted Root CA» certificates from the Trusted Root and Intermediate Certification Authorities stores?",
        ["Удаление сертификата Минцифры…"] = "Removing the Mincifry certificate…",
        ["Удаление «{0}»…"] = "Uninstalling «{0}»…",

        // ===================== Обслуживание =====================
        ["Чтение состояния (диски, CompactOS, точки восстановления)…"] = "Reading state (drives, CompactOS, restore points)…",
        ["Изменение индексации поиска"] = "Changing search indexing",
        ["Пометить как «не индексировать»: {0}"] = "Mark as «do not index»: {0}",
        ["Включить индексацию (снять пометку): {0}"] = "Enable indexing (remove the mark): {0}",
        ["Служба Windows Search полностью не отключается. Применить изменения?"] = "The Windows Search service is not fully disabled. Apply the changes?",
        ["Изменение атрибутов индексации на дисках…"] = "Changing indexing attributes on the drives…",
        ["Вернуть систему в состояние точки восстановления?\n\n{0}\n\nWindows инициирует откат и перезагрузит компьютер: открытые программы могут быть закрыты, изменения в реестре и системных файлах после точки будут отменены. Личные файлы не затрагиваются. Продолжить?"] =
            "Roll the system back to the selected restore point?\n\n{0}\n\nWindows will start the rollback and reboot the computer: open programs may be closed, and registry and system file changes made after the point will be undone. Personal files are not affected. Continue?",
        ["Проверка целостности Windows"] = "Windows integrity check",
        ["Будут выполнены последовательно:\n1) DISM /Online /Cleanup-Image /RestoreHealth\n2) sfc /scannow\n\nОперация может занять от 10 минут до часа. Прерывать не рекомендуется.\nЗапустить?"] = "The following will run in sequence:\n1) DISM /Online /Cleanup-Image /RestoreHealth\n2) sfc /scannow\n\nThe operation may take from 10 minutes to an hour. Interrupting is not recommended.\nRun it?",
        ["Запустить DISM + SFC"] = "Run DISM + SFC",
        ["Точка восстановления"] = "Restore point",
        ["Создать точку восстановления системной защиты (тип «изменение настроек»)?\nОтличная страховка перед глубокими изменениями: службы, автозагрузка, реестр."] = "Create a system protection restore point (type «modify settings»)?\nA great safety net before deep changes: services, startup, registry.",
        ["Создать точку"] = "Create point",
        ["Очистка хранилища компонентов с /ResetBase"] = "Component store cleanup with /ResetBase",
        ["DISM удалит все заменённые версии компонентов и заменит текущие.\n\nПосле /ResetBase установленные обновления Windows БОЛЬШЕ НЕЛЬЗЯ УДАЛИТЬ (не будет работать «Удалить обновление»).\nТочка восстановления перед операцией — хорошая идея. Запустить?"] = "DISM will delete all superseded component versions and replace the current ones.\n\nAfter /ResetBase, installed Windows updates CAN NO LONGER BE UNINSTALLED («Uninstall update» will not work).\nA restore point before the operation is a good idea. Run it?",
        ["Очистить с ResetBase"] = "Clean up with ResetBase",
        ["Очистка хранилища компонентов"] = "Component store cleanup",
        ["DISM /StartComponentCleanup удалит заменённые версии компонентов WinSxS.\nОперация долгая (10–40 минут), не прерывайте без необходимости.\nЗапустить?"] = "DISM /StartComponentCleanup will remove superseded WinSxS component versions.\nThe operation is long (10–40 minutes); do not interrupt unnecessarily.\nRun it?",
        ["Очистить"] = "Clean up",

        // ===================== Ввод и игры =====================
        ["Чтение параметров ввода и игр…"] = "Reading input and game settings…",
        ["Отключение Game Bar и DVR…"] = "Disabling Game Bar and DVR…",
        ["Game Bar и DVR"] = "Game Bar and DVR",
        ["Отключить Game Bar и фоновую запись (DVR)?\nGame Mode останется включённым — это полезно для игр."] = "Disable Game Bar and background recording (DVR)?\nGame Mode stays enabled — it is useful for games.",
        ["Перезапуск проводника"] = "Restarting Explorer",
        ["Задержка меню: {0} мс…"] = "Menu delay: {0} ms…",

        // ===================== Обновления =====================
        ["Чтение состояния служб обновления…"] = "Reading the update services state…",
        ["Состояние обновлений ещё не загружено."] = "The update state has not been loaded yet.",
        ["Приостановка обновлений на 7 дней…"] = "Pausing updates for 7 days…",
        ["Обновления приостановлены на 7 дней."] = "Updates are paused for 7 days.",
        ["Снятие паузы обновлений…"] = "Resuming updates…",
        ["Пауза снята."] = "Pause removed.",
        ["Сохранение состояния служб…"] = "Saving the services state…",
        ["Блокировка обновлений Windows"] = "Blocking Windows Update",
        ["Состояние служб wuauserv, UsoSvc, WaaSMedicSvc, DoSvc, BITS будет сохранено в бэкап,\nзатем службы переведены в «Отключена».\nWindows перестанет ставить обновления до команды «Вернуть обновления».\nРекомендуется создать точку восстановления (раздел «Поиск и целостность»). Заблокировать?"] = "The state of wuauserv, UsoSvc, WaaSMedicSvc, DoSvc and BITS will be saved to a backup,\nthen the services will be set to «Disabled».\nWindows will stop installing updates until you press «Unblock updates».\nCreating a restore point first (the «Search & Integrity» section) is recommended. Block?",
        ["Заблокировать"] = "Block",
        ["Обновления заблокированы. Вернуть — кнопкой «Вернуть обновления»."] = "Updates are blocked. Use «Unblock updates» to revert.",
        ["Бэкап служб не найден — блокировка не выполнялась из приложения."] = "The services backup was not found — blocking was not performed by the app.",
        ["Блокировка отменена: не удалось сохранить состояние служб (код {0})."] = "Blocking cancelled: failed to save the services state (code {0}).",
        ["Драйверы снова устанавливаются через Windows Update."] = "Drivers are installed via Windows Update again.",
        ["Авто-установка драйверов через Windows Update запрещена."] = "Automatic driver installation via Windows Update is disabled.",
        ["Разрешение установки драйверов через Windows Update…"] = "Allowing driver installation via Windows Update…",
        ["Запрет авто-драйверов…"] = "Disabling automatic drivers…",
        ["Блокировка: "] = "Blocking: ",
        ["Службы обновления отключены"] = "update services are disabled",
        ["Службы обновления возвращены к исходному состоянию."] = "Update services have been restored to their original state.",

        // ===================== Задачи =====================
        ["Чтение задач планировщика…"] = "Reading scheduled tasks…",
        ["Сохранение бэкапа задач…"] = "Saving the tasks backup…",
        ["Восстановление задач из бэкапа…"] = "Restoring tasks from the backup…",
        ["Список задач ещё не загружен."] = "The tasks list has not been loaded yet.",
        ["Не удалось прочитать задачи: "] = "Failed to read tasks: ",
        ["Задача отключена. "] = "Task disabled. ",
        ["Бэкап не сохранён (код {0}): {1}"] = "Backup not saved (code {0}): {1}",
        ["Нет файла бэкапа для отката."] = "No backup file to restore from.",
        ["бэкап ещё не создавался"] = "no backup has been created yet",
        ["манифест ещё не создавался"] = "no manifest has been created yet",
        ["Полный путь: {0}"] = "Full path: {0}",
        ["Источник: {0}\nКоманда: {1}"] = "Source: {0}\nCommand: {1}",
        ["Пакеты: {0}"] = "Packages: {0}",
        ["Удаление необратимо в рамках этой утилиты.\nПереустановка возможна только средствами Windows/Store/winget."] =
            "Removal is irreversible within this utility.\nReinstallation is only possible via Windows/Store/winget.",
        ["Удаление изменяет состав установленных компонентов; перед операцией приложение показывает подтверждение."] =
            "Removal changes the set of installed components; the app shows a confirmation before the operation.",
        ["\nОтключайте только те категории, последствия которых вам понятны; после операции исходные значения сохраняются в бэкап."] =
            "\nOnly disable categories whose consequences you understand; the original values are saved to a backup after the operation.",
        ["Дамп памяти (MEMORY.DMP)"] = "Memory dump (MEMORY.DMP)",
        ["DiagTrack, AllowTelemetry, CEIP, рекламный ID, советы и контент."] =
            "DiagTrack, AllowTelemetry, CEIP, advertising ID, tips and content.",
        ["Тосты, центр уведомлений, советы и предложения Windows."] =
            "Toasts, notification center, Windows tips and suggestions.",
        ["Глобальный запрет фоновой активности UWP и служба embeddedmode."] =
            "Global block of UWP background activity and the embeddedmode service.",
        ["Policy-отключение Copilot и анализа данных AI."] =
            "Policy-based disabling of Copilot and AI data analysis.",
        ["DODownloadMode=0 и служба DoSvc."] = "DODownloadMode=0 and the DoSvc service.",
        ["Отключение задач Compatibility Appraiser, Consolidator, QueueReporting и др."] =
            "Disables the Compatibility Appraiser, Consolidator, QueueReporting and other tasks.",
        ["Отключение задачи"] = "Disabling a task",
        ["Отключить задачу «{0}»?\nПуть: {1}"] = "Disable the task «{0}»?\nPath: {1}",
        ["Нет задачи"] = "No task",
        ["Готова"] = "Ready",
        ["Выполняется"] = "Running",

        // Назначение задач планировщика (подпись под названием в разделе «Задачи»)
        ["Собирает данные о программах и драйверах для телеметрии совместимости Windows."] =
            "Collects program and driver data for Windows compatibility telemetry.",
        ["Собирает телеметрию о файлах и программах для оценки совместимости обновлений."] =
            "Collects telemetry about files and programs to assess update compatibility.",
        ["Следит за приложениями автозагрузки и показывает уведомления об их влиянии на запуск."] =
            "Monitors startup apps and shows notifications about their startup impact.",
        ["Собирает данные Autochk после внезапного выключения ПК."] =
            "Collects Autochk data after an unexpected PC shutdown.",
        ["Отправляет данные программы улучшения качества Windows (CEIP)."] =
            "Sends Windows Customer Experience Improvement Program (CEIP) data.",
        ["Отправляет данные об использовании USB-устройств в рамках CEIP."] =
            "Sends USB device usage data as part of CEIP.",
        ["Собирает диагностические данные о дисках для отчётов Windows."] =
            "Collects disk diagnostic data for Windows reports.",
        ["Отправляет журналы использования и данные обратной связи Windows."] =
            "Sends Windows usage logs and feedback data.",
        ["Отправляет данные обратной связи Windows при скачивании сценариев."] =
            "Sends Windows feedback data when scenarios are downloaded.",
        ["Показывает всплывающие уведомления Карт Windows."] =
            "Shows Windows Maps toast notifications.",
        ["Проверяет и загружает обновления офлайн-карт."] =
            "Checks for and downloads offline map updates.",
        ["Проверяет настройки семейной безопасности (родительский контроль)."] =
            "Checks Microsoft Family Safety (parental controls) settings.",
        ["Синхронизирует настройки семейной безопасности с аккаунтом Microsoft."] =
            "Syncs Family Safety settings with the Microsoft account.",
        ["Отправляет накопленные отчёты об ошибках Windows (WER)."] =
            "Sends queued Windows Error Reporting (WER) reports.",
        ["Синхронизирует облачные сохранения игр Xbox Live."] =
            "Syncs Xbox Live cloud game saves.",

        // ===================== Прочее =====================
        ["Без прав администратора — изменение недоступно"] = "no administrator rights — changes are unavailable",
        ["Бэкап сохранён: "] = "Backup saved: ",
        ["да"] = "yes",
        ["есть"] = "yes",
        ["нет"] = "no",
        ["установлено"] = "installed",
        ["отсутствует"] = "missing",
        ["отключено"] = "disabled",
        ["отключена"] = "disabled",
        ["включено. "] = "enabled. ",
        ["включение…"] = "enabling…",
        ["выключено. "] = "disabled. ",
        ["выключение…"] = "disabling…",
        [": отключено. "] = ": disabled. ",
        [": включено. "] = ": enabled. ",
        ["не активна"] = "not active",
        ["Ошибка: "] = "Error: ",
        ["Ошибка (код "] = "Error (code ",
        ["Ошибка (код {0}): {1}"] = "Error (code {0}): {1}",

        // ===================== Диалоги и статусы (ключи правок) =====================
        ["Ослабить"] = "Weaken",
        ["Перезапустить"] = "Restart",
        ["Перезапустить explorer.exe?\nПанель задач и открытые окна проводника на секунды исчезнут."] =
            "Restart explorer.exe?\nThe taskbar and open Explorer windows will disappear for a few seconds.",
        ["Удалить закреплённые значки и настройки панели задач?\nПроводник будет перезапущен. Вернуть их можно кнопкой «Восстановить панель задач»."] =
            "Remove pinned icons and taskbar settings?\nExplorer will be restarted. They can be restored with the «Restore taskbar» button.",
        ["Восстановить закрепления и настройки панели задач из бэкапа\n(снимок на момент последней очистки)? Проводник будет перезапущен."] =
            "Restore taskbar pinning and settings from the backup\n(a snapshot from the last cleanup)? Explorer will be restarted.",
        ["ВНИМАНИЕ: операция помечена как НЕОБРАТИМАЯ в рамках этой утилиты."] =
            "WARNING: the operation is marked as IRREVERSIBLE within this utility.",
        ["Удалить всё"] = "Delete all",
        ["NetBIOS over TCP/IP"] = "NetBIOS over TCP/IP",
        ["Отключить NetBIOS over TCP/IP на всех активных IPv4-интерфейсах?"] =
            "Disable NetBIOS over TCP/IP on all active IPv4 interfaces?",
        ["Вернуть NetBIOS over TCP/IP к значению по DHCP (0) на всех активных IPv4-интерфейсах?"] =
            "Restore NetBIOS over TCP/IP to the DHCP value (0) on all active IPv4 interfaces?",
        ["Служба: {0}\nСостояние: {1}\nТип запуска: {2}\nОтложенный запуск: {3}"] =
            "Service: {0}\nState: {1}\nStart type: {2}\nDelayed start: {3}",
        ["Хранилище компонентов ещё не анализировалось."] = "Component store has not been analyzed yet.",
        ["Список точек не загружен."] = "Restore point list not loaded.",

        // ===================== Раздел 0 «Состояние ПК» (Dashboard) =====================
        ["Состояние ПК"] = "PC Status",
        ["Сводка состояния ПК, рекомендации и недавние события"] = "PC state summary, recommendations and recent events",
        ["Состояние системы: OK"] = "System state: OK",
        ["Проверка не выполнялась"] = "No scan has been run yet",
        ["Последняя проверка: {0}"] = "Last scan: {0}",
        ["Проверка ПК"] = "PC scan",
        ["Областей с ошибками: {0}"] = "Areas with errors: {0}",
        ["Нет данных"] = "No data",

        // Карточки областей
        ["Система"] = "System",
        ["Диски"] = "Disks",
        ["Автозагрузка"] = "Startup",
        ["Службы"] = "Services",
        ["Задачи"] = "Tasks",
        ["Сеть"] = "Network",
        ["Питание"] = "Power",
        ["Приватность"] = "Privacy",
        ["Обновления"] = "Updates",
        ["Диск {0}: свободно {1:0.0} ГБ из {2:0.0} ГБ"] = "Drive {0}: {1:0.0} GB free of {2:0.0} GB",
        ["Всего дисков: {0}"] = "Total drives: {0}",
        ["Элементов автозагрузки: {0}"] = "Startup entries: {0}",
        ["В норме {0} из {1} (отключено {2})"] = "{0} of {1} in normal state ({2} disabled)",
        ["Задач: {0} (отключено {1})"] = "Tasks: {0} ({1} disabled)",
        ["Задач: {0}"] = "Tasks: {0}",
        ["Активная схема: {0}"] = "Active plan: {0}",
        ["UAC: стандартный уровень"] = "UAC: standard level",
        ["UAC: ослаблен"] = "UAC: weakened",
        ["Категорий отключено: {0} из {1}"] = "Categories disabled: {0} of {1}",
        ["Обновления заблокированы"] = "Updates are blocked",
        ["Обновления приостановлены"] = "Updates are paused",
        ["Штатный режим обновлений"] = "Updates run normally",

        // Рекомендации
        ["На диске {0} свободно {1:0.0} ГБ из {2:0.0} ГБ{3}. Освободить место можно в разделе очистки."] =
            "Drive {0} has {1:0.0} GB free of {2:0.0} GB{3}. Free up space in the cleanup section.",
        [" (занято {0:0.0}%)"] = " ({0:0.0}% used)",
        ["Открыть очистку диска"] = "Open disk cleanup",
        ["Много элементов автозагрузки ({0})"] = "Many startup entries ({0})",
        ["В автозагрузке {0} элементов (порог {1}). Это не ошибка, но каждая запись может замедлять запуск Windows."] =
            "There are {0} startup entries (threshold {1}). This is not an error, but each entry may slow down Windows startup.",
        ["Открыть автозагрузку"] = "Open startup",
        ["Обновления Windows заблокированы"] = "Windows updates are blocked",
        ["Служба обновлений переведена в «Отключена». Это может быть осознанной настройкой: обновления не устанавливаются, пока блокировка не снята."] =
            "The update service is set to «Disabled». This may be an intentional setting: updates are not installed until the block is removed.",
        ["Открыть обновления"] = "Open updates",
        ["Пауза обновлений активна: до конца срока обновления не устанавливаются. Это может быть осознанной настройкой."] =
            "The update pause is active: updates are not installed until it expires. This may be an intentional setting.",
        ["Не удалось прочитать часть областей"] = "Some areas could not be read",
        ["Области: {0}. Данные этих областей в снимке отсутствуют."] =
            "Areas: {0}. Data for these areas is missing in the snapshot.",
        ["Повторить сканирование"] = "Scan again",

        // История операций
        ["Очистка"] = "Cleanup",
        ["Корзина"] = "Recycle Bin",
        ["Временные файлы"] = "Temp folders",
        ["Кэш браузеров"] = "Browser caches",
        ["Кэш обновлений"] = "Update cache",
        ["План электропитания: {0}"] = "Power plan: {0}",
        ["Игровой профиль применён"] = "Gaming profile applied",
        ["Откат игрового профиля"] = "Gaming profile rollback",
        ["Пауза обновлений (7 дней)"] = "Updates paused for 7 days",
        ["Снятие паузы"] = "Pause removed",
        ["Блокировка обновлений"] = "Updates blocked",
        ["Возврат обновлений"] = "Updates unblocked",
        ["Служба: {0}"] = "Service: {0}",
        ["включена"] = "enabled",
        ["Отключение задачи: {0}"] = "Task disabled: {0}",
        ["Мусорное ПО"] = "Bloatware",
        ["Удаление: {0}"] = "Removed: {0}",
        ["Массовое удаление"] = "Bulk removal",

        // ===================== Этап 2: diff, baseline, статистика, история =====================

        // Раздел «История» (навигация и фильтр)
        ["История"] = "History",
        ["Полная история операций по категориям"] = "Full operation history by category",
        ["Все категории"] = "All categories",

        // Названия полей diff снимков (SnapshotDiffService → DiffRow)
        ["Версия Windows"] = "Windows version",
        ["Сборка Windows"] = "Windows build",
        ["Процессор"] = "CPU",
        ["Оперативная память"] = "RAM",
        ["Видеоадаптер"] = "GPU",
        ["Свободно на {0}"] = "Free on {0}",
        ["Элементов автозагрузки"] = "Startup entries",
        ["Служб в норме"] = "Services OK",
        ["Служб изменено"] = "Services changed",
        ["Служб всего"] = "Services total",
        ["Задач всего"] = "Tasks total",
        ["Задач отключено"] = "Tasks disabled",
        ["Сеть: Auto-Tuning"] = "Network: Auto-Tuning",
        ["Сеть: ECN"] = "Network: ECN",
        ["Сеть: QoS"] = "Network: QoS",
        ["Сеть: NetBIOS"] = "Network: NetBIOS",
        ["Схема электропитания"] = "Power plan",
        ["Пауза обновлений"] = "Updates paused",
        ["UAC"] = "UAC",
        ["Приватность: отключено"] = "Privacy: disabled",
        ["Приватность: всего"] = "Privacy: total",

        // Значения diff (UAC, «было неизвестно», единицы ГБ)
        ["неизвестно"] = "unknown",
        ["стандартный"] = "standard",
        ["ослаблен"] = "weakened",
        ["{0:0.0} ГБ"] = "{0:0.0} GB",

        // Baseline
        ["Параметров отличаются от baseline: {0}"] = "Parameters differ from baseline: {0}",
        ["Отличий от baseline нет"] = "No differences from the baseline",

        // Статистика операций
        ["Проверок ПК: {0}"] = "PC scans: {0}",
        ["Ошибок: {0}"] = "Errors: {0}",
        ["Освобождено: {0}"] = "Freed: {0}",
        ["{0:0.0} МБ"] = "{0:0.0} MB",
        ["{0:0.0} КБ"] = "{0:0.0} KB",
        ["{0} Б"] = "{0} B",

        // События истории (автозагрузка, точка восстановления)
        ["Элемент автозагрузки: {0}"] = "Startup entry: {0}",
        ["Бэкап"] = "Backup",
        ["Бэкап: {0}"] = "Backup: {0}",
        ["Ошибка"] = "Error",
        ["Неизвестная ошибка при получении информации о системе."] =
            "Unknown error while getting system information.",
        ["Внутренняя ошибка: {0}"] = "Internal error: {0}",

        // ===================== Профиль (§33.8) =====================
        ["Профиль"] = "Profile",
        ["Применение профиля"] = "Applying profile",
        ["Соответствуют профилю: {0} из {1}"] = "Matching the profile: {0} of {1}",
        ["Изменять нечего: параметры уже соответствуют профилю."] =
            "Nothing to change: the settings already match the profile.",
        ["Будут изменены параметры:\n{0}\n\nПродолжить?"] =
            "The following settings will be changed:\n{0}\n\nContinue?",
        ["{0}: {1} → {2}"] = "{0}: {1} → {2}",
        ["Цель: {0}"] = "Target: {0}",
        ["Цель: {0}. Ошибка: {1}"] = "Target: {0}. Error: {1}",
        ["Профиль применён: шагов изменено {0}."] = "Profile applied: {0} steps changed.",
        ["Профиль применён частично: шагов изменено {0} из {1}."] =
            "Profile applied partially: {0} of {1} steps changed.",
        ["Профиль не применён: ни один шаг не изменён."] =
            "Profile was not applied: no step was changed.",
        ["включить"] = "turn on",
        ["выключить"] = "turn off",
        ["включено"] = "on",
        ["выключено"] = "off",
        ["Не удалось прочитать"] = "Could not read",

        // Названия шагов профиля (категории — общие ключи выше: Питание/Сеть/Игры/Приватность)
        ["Игры"] = "Games",
        ["Максимальная производительность (питание)"] = "Ultimate performance (power plan)",
        ["Сбалансированная схема питания"] = "Balanced power plan",
        ["Игровой профиль сети"] = "Gaming network profile",
        ["Game Bar"] = "Game Bar",
        ["Фоновая запись игр (Game DVR)"] = "Background game recording (Game DVR)",
        // ===== Раздел 22 «Браузер» =====
        ["Компонент браузера не установлен."] = "Browser component is not installed.",
        ["WebView2 Runtime не найден"] = "WebView2 Runtime not found",
        ["WebView2 Runtime: {0}"] = "WebView2 Runtime: {0}",
        ["Запуск браузера…"] = "Starting browser…",
        ["Не удалось запустить браузер. Подробности в журнале."] = "Failed to start the browser. See the log for details.",
        ["Не удалось запустить браузер: {0}"] = "Failed to start the browser: {0}",
        ["WebView2 Runtime установлен. Нажмите «Запустить»."] = "WebView2 Runtime installed. Press «Launch».",
        ["Установить WebView2 Runtime не удалось. Проверьте интернет и повторите."] = "Could not install WebView2 Runtime. Check your internet connection and retry.",
        ["Скачивание WebView2 Runtime…"] = "Downloading WebView2 Runtime…",
        ["Установка WebView2 Runtime…"] = "Installing WebView2 Runtime…",
        ["Новая вкладка"] = "New tab",
        ["Достигнут предел вкладок ({0})."] = "Tab limit reached ({0}).",
        ["Не удалось открыть вкладку: {0}"] = "Failed to open a tab: {0}",
        ["Навигация заблокирована: {0}"] = "Navigation blocked: {0}",
        ["Навигация заблокирована политикой безопасности."] = "Navigation blocked by the security policy.",
        ["Не удалось подключиться к интернету."] = "Could not connect to the internet.",
        ["Ошибка сертификата сайта."] = "Site certificate error.",
        ["Страница недоступна."] = "Page is unavailable.",
        ["Браузерная вкладка была перезапущена."] = "The browser tab was restarted.",
        ["Завершено"] = "Completed",
        ["Отменено"] = "Canceled",
        ["Проверяется"] = "Checking",
        ["Угроза"] = "Threat",
        ["Файл скачан: {0}. Он не был запущен автоматически."] = "File downloaded: {0}. It was not launched automatically.",
        ["Загрузка не удалась: {0}."] = "Download failed: {0}.",
        ["Загрузка не удалась: небезопасное имя файла."] = "Download failed: unsafe file name.",
        ["Открыть файл"] = "Open file",
        ["Запустить"] = "Run",
        ["Запуск скрипта"] = "Script run",
        ["Скрипт «{0}» выполнится с правами текущего сеанса SCU, включая администратора. Запустить?"] =
            "Script «{0}» will run with the rights of the current SCU session, including administrator. Run it?",
        ["Отменено."] = "Canceled.",
        ["Файл скрипта не найден или не читается — запуск отменён."] =
            "Script file is missing or unreadable — run canceled.",
        ["Файл скрипта изменён после добавления — запуск отменён."] =
            "Script file was modified after import — run canceled.",
        ["Не удалось открыть файл: {0}"] = "Failed to open the file: {0}",
        ["встроенная проверка недоступна"] = "built-in check unavailable",
        ["обнаружено угроз: {0}"] = "threats found: {0}",
        ["угроз не обнаружено"] = "no threats found",
        ["ошибка проверки"] = "check failed",
        ["В скачанном файле {0} обнаружены угрозы. Файл не запускался."] = "Threats were found in the downloaded file {0}. The file was not launched.",
        ["Внешнее приложение"] = "External application",
        ["Не удалось открыть внешнее приложение: {0}"] = "Failed to launch the external application: {0}",
        ["Закладка удалена."] = "Bookmark removed.",
        ["Закладка добавлена."] = "Bookmark added.",
        ["История браузера очищена."] = "Browser history cleared.",
        ["История и данные сайтов очищены."] = "Browser history and site data cleared.",
        ["Масштаб: {0}%"] = "Zoom: {0}%",
        ["Выбор папки загрузок"] = "Choose download folder",
        ["Открыть папку"] = "Open folder",
        ["Открыть"] = "Open",
        ["нет соединения с GitHub"] = "no connection to GitHub",
        ["UAC отключён"] = "UAC disabled",
        ["UAC отключён или ослаблен — меньше запросов на повышение прав. Управляется в разделе «Безопасность»."] = "UAC disabled or weakened — fewer elevation prompts. Managed in the «Security» section.",
        ["отключён"] = "disabled",
        ["Переключатели приватности включены"] = "Privacy toggles enabled",
        ["Доля включённых переключателей в разделе «Приватность и телеметрия»."] = "Share of enabled toggles in the «Privacy and telemetry» section.",
        ["Скрипт добавлен"] = "Script added",
        ["«{0}» размещён в разделе «{1}»."] = "«{0}» placed in the «{1}» section.",
        ["Скрипт не добавлен"] = "Script not added",
        ["Скрипт не рабочий"] = "The script is broken",
        ["Файл не найден."] = "File not found.",
        ["Поддерживаются только .ps1 и .bat."] = "Only .ps1 and .bat are supported.",
        ["Скрипт пустой."] = "The script is empty.",
        ["Файл повреждён (бинарные данные)."] = "The file is corrupted (binary data).",
        ["Файл не читается: {0}"] = "The file cannot be read: {0}",
        ["Не удалось запустить проверку (PowerShell)."] = "Could not run the check (PowerShell).",
        ["Готово."] = "Done.",
        ["без вывода"] = "no output",
        ["Код {0}: {1}"] = "Exit code {0}: {1}",
        ["Выбор скрипта"] = "Choose a script",
        ["Скрипты (*.ps1;*.bat)|*.ps1;*.bat|Все файлы (*.*)|*.*"] = "Scripts (*.ps1;*.bat)|*.ps1;*.bat|All files (*.*)|*.*",
        ["Скрипт сохранён и проверен — рабочий."] = "Script saved and verified — it works.",
        ["Скрипт сохранён, но проверка не пройдена: {0}"] = "Script saved, but verification failed: {0}",
        ["Раздел: {0}"] = "Section: {0}",
        ["Мои утилиты"] = "My utilities",
        ["Далее"] = "Next",
        ["Готово"] = "Finish",
        ["Ошибка: {0}"] = "Error: {0}",
        ["Проверка наличия обновлений…"] = "Checking for updates…",
        ["Не удалось проверить обновления: {0}"] = "Could not check for updates: {0}",
        ["Доступна новая версия: SCU {0} (установлена {1})."] = "A new version is available: SCU {0} (installed {1}).",
        ["Обновление не требуется: у вас актуальная версия ({0})."] = "No update needed: you have the latest version ({0}).",
        ["Доступна новая версия SCU"] = "New SCU version available",
        ["Установлена {0}, доступна {1}. Откройте страницу релизов, чтобы обновиться."] = "Installed {0}, available {1}. Open the releases page to update.",
        ["Не удалось открыть ссылку: "] = "Could not open the link: ",
        ["Глобальные горячие клавиши"] = "Global hotkeys",
        ["Ctrl+Alt+S — показать или скрыть окно SCU из любого приложения"] = "Ctrl+Alt+S — show or hide the SCU window from any application",
        ["Ctrl+1…Ctrl+9 — быстрый переход к разделам по порядку меню"] = "Ctrl+1…Ctrl+9 — quick navigation to sections in menu order",
        ["Загрузка завершена"] = "Download completed",
        ["{0} — файл не был запущен автоматически."] = "{0} — the file was not launched automatically.",
        ["Сканер: угрозы в загрузке"] = "Scanner: threats in download",
        ["В файле {0} обнаружены угрозы ({1}). Файл не запускался."] = "Threats were found in {0} ({1}). The file was not launched.",
        ["Сканер: обнаружены угрозы"] = "Scanner: threats found",
        ["Проверка «{0}»: проблемных объектов — {1}."] = "Scan of «{0}»: {1} problem items found.",
        ["Компонент установлен"] = "Component installed",
        ["WebView2 Runtime установлен — встроенный браузер готов к запуску."] = "WebView2 Runtime installed — the built-in browser is ready to launch.",
        ["HOTKEY | register failed | Ctrl+Alt+S занят другим приложением"] = "HOTKEY | register failed | Ctrl+Alt+S is taken by another application",

        ["Оценка объёма…"] = "Estimating size…",
        ["Осталось ≈ оценка…"] = "Remaining ≈ estimating…",
        ["Осталось ≈ {0}"] = "Remaining ≈ {0}",
        ["Прервано"] = "Interrupted",
        ["Создание браузерной среды не завершилось за отведённое время."] = "Browser environment creation did not finish within the time limit.",
        ["Не удалось запустить браузер: {0} Проверьте окружение и нажмите «Запустить» ещё раз."] = "Failed to start the browser: {0} Check the environment and press «Launch» again.",
        ["SCU запущен от имени другого пользователя. Известное ограничение WebView2: браузер может не запуститься — при зависании перезапустите SCU обычным способом."] = "SCU is running as a different user. Known WebView2 limitation: the browser may fail to start — if it hangs, restart SCU in the normal way.",
        ["Не удалось проверить WebView2 Runtime: {0}"] = "Could not verify WebView2 Runtime: {0}",
        ["Проверка подписи установщика не пройдена — установка отменена."] = "Installer signature check failed — installation canceled.",
        ["Не удалось открыть ссылку: недопустимый адрес."] = "Could not open the link: invalid address.",
        ["Адрес заблокирован политикой безопасности."] = "The address is blocked by the security policy.",
        ["Открыть файл «{0}», скачанный из интернета?"] = "Open file «{0}» downloaded from the internet?",
        ["Сайт запрашивает открытие внешнего приложения — подтверждение уже показано."] = "The site requests an external application — the confirmation has already been shown.",
        ["Идёт загрузка"] = "Downloading",
        ["Ошибка загрузки"] = "Download error",

        // ===================== Раздел 23 «Устранение неполадок» =====================

        // Probe-заголовки прогресса.
        ["Проверка системы"] = "System check",
        ["Проверка дисков"] = "Disk check",
        ["Проверка служб"] = "Services check",
        ["Проверка Windows Update"] = "Windows Update check",
        ["Проверка сети"] = "Network check",
        ["Проверка автозагрузки"] = "Startup check",
        ["Проверка безопасности"] = "Security check",
        ["Проверка событий"] = "Event log check",
        ["Проверка системных файлов"] = "System files check",
        ["Папка автозагрузки (пользователь)"] = "Startup folder (user)",
        ["Папка автозагрузки (общая)"] = "Startup folder (common)",

        // Группы и сводка.
        ["Проблемы"] = "Problems",
        ["Предупреждения"] = "Warnings",
        ["Информация"] = "Information",
        ["Проблем не найдено"] = "No problems found",
        // П. SCAN-01: честные заголовки результата сканирования.
        ["Сканирование прервано. Частичный результат сохранён."] =
            "Scan was cancelled. Partial results are kept.",
        ["Сканирование завершено с ошибками: часть файлов не проверена."] =
            "Scan finished with errors: some files were not checked.",
        ["Обнаружено проблем: {0}."] = "Problems found: {0}.",
        ["Обнаружено проблем: {0} (сканирование неполное, пропущено {1})."] =
            "Problems found: {0} (scan incomplete, {1} skipped).",
        ["Обнаружений не найдено, но сканирование неполное: пропущено {0}."] =
            "No detections, but the scan is incomplete: {0} skipped.",
        ["На момент сканирования обнаружений не найдено."] =
            "No detections were found during the scan.",
        ["Неполное"] = "Incomplete",
        ["Требуют внимания: {0}"] = "Require attention: {0}",
        ["Диагностика ещё не выполнялась"] = "Diagnostics have not been run yet",
        ["Проверено: {0}"] = "Checked: {0}",
        ["Диагностика ещё не выполнялась."] = "Diagnostics have not been run yet.",
        ["Серьёзных проблем не обнаружено"] = "No serious problems found",
        ["Найдено {0} состояний, требующих внимания"] = "Found {0} states requiring attention",
        ["Проверено областей: {0} / {1}."] = "Areas checked: {0} / {1}.",
        ["Проблем: {0}, предупреждений: {1}, информации: {2}."] = "Problems: {0}, warnings: {1}, informational: {2}.",
        ["Сбор диагностических данных…"] = "Collecting diagnostic data…",
        ["Проверка остановлена. Проверено областей: {0} / {1}."] = "Check stopped. Areas checked: {0} / {1}.",
        ["Диагностика завершена. {0}"] = "Diagnostics completed. {0}",
        ["Пропущенные состояния ({0})"] = "Skipped states ({0})",
        ["Состояние «{0}» скрыто — вернуть можно в списке пропущенных."] = "The state «{0}» is hidden — restore it from the skipped list.",
        ["Состояние «{0}» возвращено."] = "The state «{0}» is restored.",
        ["Выполнение: {0}…"] = "Running: {0}…",
        ["Повторная проверка…"] = "Re-checking…",
        ["Готово. Проверка подтвердила устранение: {0}"] = "Done. The re-check confirmed the fix: {0}",
        ["Готово. {0}"] = "Done. {0}",
        ["Исправление не выполнено: {0}"] = "The fix was not applied: {0}",
        ["Нужны права администратора — исправление недоступно."] = "Administrator rights are required — the fix is unavailable.",
        ["Подтверждение исправления"] = "Confirm fix",
        ["Выполнить «{0}»?"] = "Run «{0}»?",
        ["Не удалось открыть {0}: {1}"] = "Failed to open {0}: {1}",

        // Диск.
        ["Ошибки файловой системы в журнале событий"] = "File system errors in the event log",
        ["За последние 7 дней журнал записал ошибки поставщиков Disk/Ntfs. Возможны проблемы с файловой системой или диском."] =
            "Over the last 7 days the log recorded Disk/Ntfs provider errors. There may be problems with the file system or the disk.",
        ["Ошибки файловой системы могут приводить к сбоям чтения, зависаниям и потере данных."] =
            "File system errors can cause read failures, hangs and data loss.",
        ["Журнал не содержит ошибок Disk/Ntfs"] = "The log contains no Disk/Ntfs errors",
        ["Ошибок: {0} типов"] = "Errors: {0} types",
        ["Открыть очистку"] = "Open Cleanup",
        ["Открыть «Поиск и целостность»"] = "Open «Search & Integrity»",
        ["Мало свободного места на системном диске"] = "Low free space on the system drive",
        ["На {0} свободно всего {1:0.0} ГБ."] = "Only {1:0.0} GB free on {0}.",
        ["Недостаток места мешает обновлениям, временным файлам и нормальной работе Windows."] =
            "Lack of space interferes with updates, temporary files and normal Windows operation.",
        ["Свободно не менее 10 ГБ"] = "At least 10 GB free",
        ["Свободно {0}"] = "Free: {0}",
        ["Свободно"] = "Free",
        ["Всего"] = "Total",
        ["Диск"] = "Drive",

        // Windows Update.
        ["Обновления Windows отключены"] = "Windows Update is disabled",
        ["Служба Windows Update переведена в состояние «Отключена» — система не будет устанавливать обновления."] =
            "The Windows Update service is set to «Disabled» — the system will not install updates.",
        ["Отключённые обновления — частая осознанная настройка. Включайте, только если нужен штатный Update."] =
            "Disabled updates are a common deliberate choice. Enable them only if you want the stock Update.",
        ["Служба wuauserv разрешена к запуску"] = "The wuauserv service is allowed to start",
        ["Служба wuauserv отключена"] = "The wuauserv service is disabled",
        ["Отключена"] = "Disabled",
        ["Открыть «Обновления Windows»"] = "Open «Windows Update»",
        ["Служба Windows Update не работает"] = "The Windows Update service is not running",
        ["Служба wuauserv остановлена, но не отключена — установка обновлений не выполняется."] =
            "The wuauserv service is stopped but not disabled — updates are not being installed.",
        ["Без этой службы Windows не получает обновления безопасности."] =
            "Without this service Windows does not receive security updates.",
        ["Служба wuauserv работает"] = "The wuauserv service is running",
        ["Служба wuauserv остановлена"] = "The wuauserv service is stopped",
        ["Остановлена"] = "Stopped",
        ["Запустить службу {0}"] = "Start service {0}",
        ["Обновления Windows приостановлены"] = "Windows Update is paused",
        ["Пауза обновлений активна."] = "The update pause is active.",
        ["Пауза обновлений — частая осознанная настройка; это состояние, а не ошибка."] =
            "Pausing updates is a common deliberate choice; this is a state, not an error.",
        ["Пауза активна"] = "Pause active",
        ["Повторяющиеся ошибки установки обновлений"] = "Recurring update installation errors",
        ["За последние 7 дней журнал зафиксировал {0} ошибок WindowsUpdateClient."] =
            "Over the last 7 days the log recorded {0} WindowsUpdateClient errors.",
        ["Обновления могут устанавливаться циклично или не устанавливаться вовсе."] =
            "Updates may install in a loop or not install at all.",

        // Сеть.
        ["Активный сетевой адаптер не найден"] = "No active network adapter found",
        ["Все сетевые адаптеры отключены или не подключены к сети."] =
            "All network adapters are disabled or not connected to a network.",
        ["Без активного адаптера недоступны сеть и интернет."] =
            "Without an active adapter, the network and internet are unavailable.",
        ["Есть адаптер в состоянии Up"] = "An Up adapter is present",
        ["Активных адаптеров нет"] = "No active adapters",
        ["Нет активных"] = "None active",
        ["Не найден шлюз по умолчанию"] = "Default gateway not found",
        ["У активного адаптера не задан default gateway — доступ за пределы локальной сети невозможен."] =
            "The active adapter has no default gateway — access beyond the local network is impossible.",
        ["Без шлюза не работает выход в интернет."] = "Without a gateway, internet access does not work.",
        ["Шлюз по умолчанию задан"] = "A default gateway is set",
        ["Шлюз не найден"] = "Gateway not found",
        ["Не задан"] = "Not set",
        ["Открыть «Сеть»"] = "Open «Network»",
        ["Не выполняется разрешение DNS-имён"] = "DNS name resolution fails",
        ["Тестовое имя {0} не разрешилось в IP-адрес. Возможны проблемы с настройкой DNS."] =
            "The test name {0} did not resolve to an IP address. There may be a DNS configuration problem.",
        ["Сбой DNS внешне выглядит как «нет интернета», даже когда связь есть."] =
            "A DNS failure looks like «no internet» even when connectivity exists.",
        ["Имя разрешается"] = "The name resolves",
        ["Разрешение не удалось"] = "Resolution failed",
        ["Проверка не прошла"] = "Check failed",
        ["Сбросить кэш DNS"] = "Flush DNS cache",

        // Службы.
        ["Служба {0} отключена"] = "Service {0} is disabled",
        ["Критичная служба {0} ({1}) переведена в состояние «Отключена»."] =
            "Critical service {0} ({1}) is set to «Disabled».",
        ["Отключён Defender/центр безопасности — защита системы может не работать."] =
            "Defender/the security center is disabled — system protection may not work.",
        ["Отключённая служба может нарушать работу зависящих от неё компонентов."] =
            "A disabled service can break the components that depend on it.",
        ["Служба разрешена к запуску"] = "The service is allowed to start",
        ["Открыть «Службы Windows»"] = "Open «Windows Services»",
        ["Служба {0} не работает"] = "Service {0} is not running",
        ["Служба {0} ({1}) остановлена, хотя должна работать постоянно."] =
            "Service {0} ({1}) is stopped although it should run constantly.",
        ["Остановленная системная служба нарушает работу зависящих компонентов."] =
            "A stopped system service breaks the components that depend on it.",
        ["Служба работает"] = "The service is running",
        ["Служба остановлена"] = "The service is stopped",
        ["Повторяющиеся сбои служб в журнале"] = "Recurring service failures in the log",
        ["Service Control Manager зафиксировал {0} отказов за 7 дней."] =
            "Service Control Manager recorded {0} failures over 7 days.",
        ["Повторяющиеся отказы служб — признак проблемы в конкретном компоненте."] =
            "Recurring service failures point to a problem in a specific component.",

        // Автозагрузка.
        ["Недействительные записи автозагрузки"] = "Invalid startup entries",
        ["Найдено {0} записей автозагрузки, указывающих на отсутствующие файлы."] =
            "Found {0} startup entries pointing to missing files.",
        ["Такие записи бесполезно нагружают запуск и захламляют автозагрузку."] =
            "Such entries needlessly slow startup and clutter the startup list.",
        ["Все записи указывают на существующие файлы"] = "All entries point to existing files",
        ["Недействительных записей: {0}"] = "Invalid entries: {0}",
        ["Открыть «Автозагрузку»"] = "Open «Startup»",
        ["Дублирующиеся записи автозагрузки"] = "Duplicate startup entries",
        ["Некоторые программы прописаны в автозагрузке более одного раза."] =
            "Some programs are registered in startup more than once.",
        ["Дубликаты запускают одну и ту же программу дважды — лишняя нагрузка."] =
            "Duplicates start the same program twice — needless load.",
        ["Команда"] = "Command",

        // Безопасность.
        ["Защита в реальном времени отключена"] = "Real-time protection is off",
        ["Зарегистрированное антивирусное ПО сообщает о выключенной защите в реальном времени."] =
            "The registered antivirus reports real-time protection being off.",
        ["С отключённой защитой система открыта для вредоносного ПО."] =
            "With protection off, the system is open to malware.",
        ["Защита в реальном времени включена"] = "Real-time protection is on",
        ["Открыть «Безопасность Windows»"] = "Open «Windows Security»",
        ["Антивирусное ПО не зарегистрировано"] = "No antivirus product registered",
        ["Центр безопасности Windows не сообщает ни об одном антивирусном продукте."] =
            "Windows Security Center reports no antivirus product.",
        ["Может означать отключённый Defender либо нестандартную защиту."] =
            "May mean Defender is off or a non-standard protection is in place.",
        ["Продуктов не найдено"] = "No products found",

        // События.
        ["Повторяющиеся падения приложений"] = "Recurring application crashes",
        ["За 7 дней зафиксировано {0} аварийных завершений приложений."] =
            "Over 7 days, {0} application crashes were recorded.",
        ["Повторяющиеся падения указывают на проблемную программу или повреждённые файлы."] =
            "Recurring crashes point to a problematic program or damaged files.",
        ["Обнаружены сбои с BSOD (BugCheck)"] = "BSOD crashes detected (BugCheck)",
        ["Неожиданные перезагрузки"] = "Unexpected reboots",
        ["Система фиксирует остановку с ошибкой ядра ({0} событий за 7 дней)."] =
            "The system records kernel error stops ({0} events over 7 days).",
        ["Kernel-Power 41 зафиксировал {0} неожиданных отключений за 7 дней."] =
            "Kernel-Power 41 recorded {0} unexpected shutdowns over 7 days.",
        ["Причины: драйверы, память, питание. Нужна оценка по коду ошибки."] =
            "Causes: drivers, memory, power. The error code needs evaluation.",
        ["Аппаратные ошибки (WHEA)"] = "Hardware errors (WHEA)",
        ["WHEA-Logger зафиксировал {0} аппаратных событий за 7 дней."] =
            "WHEA-Logger recorded {0} hardware events over 7 days.",
        ["Аппаратные ошибки могут предшествовать сбоям и BSOD."] =
            "Hardware errors may precede failures and BSOD.",

        // Система.
        ["Требуется перезагрузка"] = "Reboot required",
        ["В системе есть незавершённые изменения ({0})."] = "The system has unfinished changes ({0}).",
        ["Часть установленных изменений не работает до перезагрузки."] =
            "Some applied changes do not take effect until a reboot.",
        ["Хранилище компонентов Windows помечено как повреждённое"] = "Windows component store is flagged as corrupted",
        ["DISM /CheckHealth сообщает о повреждении хранилища компонентов. Возможны сбои установки обновлений и компонентов."] =
            "DISM /CheckHealth reports component store corruption. Update and component installation may fail.",
        ["Повреждённое хранилище ломает обновления и установку компонентов."] =
            "A corrupted store breaks updates and component installation.",
        ["Повреждений не обнаружено"] = "No corruption detected",
        ["DISM сообщает о повреждении"] = "DISM reports corruption",
        ["Восстановить (DISM + SFC)"] = "Repair (DISM + SFC)",

        // Исправления.
        ["Служба {0} не найдена."] = "Service {0} not found.",
        ["Служба {0} уже работает."] = "Service {0} is already running.",
        ["Служба {0} отключена — сначала включите её в разделе «Службы Windows»."] =
            "Service {0} is disabled — enable it in «Windows Services» first.",
        ["Служба {0} запущена."] = "Service {0} started.",
        ["Не удалось запустить службу {0}: {1}"] = "Failed to start service {0}: {1}",

        // Вторая очередь: драйверы, звук, печать.
        ["Проверка драйверов"] = "Driver check",
        ["Устройства с ошибками драйверов"] = "Devices with driver errors",
        ["Диспетчер устройств сообщает о {0} устройствах с проблемами загрузки драйверов."] =
            "Device Manager reports {0} devices with driver load problems.",
        ["Устройство с problem code не работает, пока драйвер не установлен или не исправлен."] =
            "A device with a problem code does not work until its driver is installed or fixed.",
        ["Нет устройств с ошибками (код 0)"] = "No devices with errors (code 0)",
        ["Проблемных устройств: {0}"] = "Problem devices: {0}",
        ["Открыть диспетчер устройств"] = "Open Device Manager",
        ["Отключённые устройства"] = "Disabled devices",
        ["В системе есть {0} отключённых вручную устройств."] =
            "The system has {0} devices disabled manually.",
        ["Отключение устройства — осознанная настройка; это состояние, а не ошибка."] =
            "Disabling a device is a deliberate choice; this is a state, not an error.",
        ["Служба звука не работает"] = "The audio service is not running",
        ["Служба Audiosrv остановлена — звук в системе не воспроизводится."] =
            "The Audiosrv service is stopped — no sound is played on the system.",
        ["Без Windows Audio не работают ни динамики, ни наушники, ни приложения со звуком."] =
            "Without Windows Audio, neither speakers, nor headphones, nor sound apps work.",
        ["Служба Audiosrv работает"] = "The Audiosrv service is running",
        ["Служба Audiosrv остановлена"] = "The Audiosrv service is stopped",
        ["Служба печати не работает"] = "The print service is not running",
        ["Диспетчер печати (Spooler) не запущен — печать и просмотр принтеров недоступны."] =
            "The Print Spooler is not running — printing and viewing printers are unavailable.",
        ["Отключение Spooler — распространённая осознанная настройка; это состояние, а не ошибка."] =
            "Disabling the Spooler is a common deliberate choice; this is a state, not an error.",
        ["Служба Spooler работает (если нужна печать)"] = "The Spooler service is running (if printing is needed)",
        ["Служба Spooler не запущена"] = "The Spooler service is not running",

        // Вторая очередь: накопители, время.
        ["Проверка накопителей"] = "Storage check",
        ["SMART предсказывает отказ диска"] = "SMART predicts drive failure",
        ["Для {0} накопителя(ей) SMART сообщает о вероятном отказе в ближайшее время."] =
            "For {0} drive(s) SMART reports a likely failure in the near future.",
        ["Отказ диска означает потерю данных — стоит как можно скорее скопировать важное."] =
            "A drive failure means data loss — copy what matters as soon as possible.",
        ["PredictFailure = false на всех накопителях"] = "PredictFailure = false on all drives",
        ["PredictFailure = true: {0}"] = "PredictFailure = true: {0}",
        ["Открыть управление дисками"] = "Open Disk Management",
        ["Синхронизация времени не работает"] = "Time synchronization is not working",
        ["Служба времени Windows (w32time) не запущена — часы могут уходить от реального времени."] =
            "The Windows Time service (w32time) is not running — the clock may drift from real time.",
        ["Расхождение часов ломает HTTPS, лицензии и планировщик."] =
            "Clock drift breaks HTTPS, licensing and the task scheduler.",
        ["Служба w32time работает (или стартует по триггеру)"] = "The w32time service is running (or trigger-started)",
        ["Служба w32time остановлена"] = "The w32time service is stopped",

        // Вторая очередь: аудио.
        ["Проверка аудио"] = "Audio check",
        ["Служба построения аудиоконечных точек не работает"] = "The audio endpoint builder service is not running",
        ["Служба AudioEndpointBuilder остановлена — аудиоустройства не будут появляться в системе."] =
            "The AudioEndpointBuilder service is stopped — audio devices will not appear in the system.",
        ["Даже при работающем Windows Audio без конечных точек звука нет: ни вывода, ни устройств в микшере."] =
            "Even with Windows Audio running, without endpoints there is no sound: no output, no devices in the mixer.",
        ["Служба AudioEndpointBuilder работает"] = "The AudioEndpointBuilder service is running",
        ["Служба AudioEndpointBuilder остановлена"] = "The AudioEndpointBuilder service is stopped",
        ["Звуковые устройства не обнаружены"] = "No sound devices detected",
        ["Windows не сообщает ни об одном звуковом устройстве. Если звука нет — проверьте подключения и BIOS."] =
            "Windows reports no sound devices. If you have no sound, check connections and BIOS.",
        ["Отсутствие устройств объясняет «нет звука»: системе просто нечего выводить."] =
            "The absence of devices explains «no sound»: the system simply has nothing to output to.",
        ["Есть хотя бы одно устройство вывода звука"] = "At least one audio output device exists",
        ["Устройств нет"] = "No devices",
        ["Звуковое устройство сообщает об ошибке"] = "A sound device reports an error",
        ["{0} звуковое(ых) устройство(й) в состоянии «Error» без problem code — вероятен сбой драйвера."] =
            "{0} sound device(s) are in the «Error» state without a problem code — a driver failure is likely.",
        ["Устройство есть, но не отвечает: звук на нём работать не будет."] =
            "The device is present but not responding: it will not produce sound.",
        ["Статус всех звуковых устройств — OK"] = "All sound devices report Status = OK",
        ["Устройств с ошибкой: {0}"] = "Devices with an error: {0}",

        // ===== Раздел 24 «DeepSeek» =====
        ["Проверка ключа API на сервере…"] = "Verifying the API key with the provider…",
        ["Подключено. Модель чата: {0}"] = "Connected. Chat model: {0}",
        ["Проверка ключа отменена."] = "Key check cancelled.",
        ["API-ключ удалён."] = "API key removed.",
        ["Удалить сохранённый API-ключ? История чатов останется, но чат потребует повторного подключения."] =
            "Remove the stored API key? Chat history will remain, but the chat will require reconnecting.",
        ["Новый чат"] = "New chat",
        ["Удалить чат"] = "Delete chat",
        ["Удалить чат «{0}» из истории? Восстановить его будет нельзя."] =
            "Delete chat «{0}» from history? It cannot be restored.",
        ["Запрос отменён."] = "Request cancelled.",
        ["Нет соединения с провайдером — проверьте интернет."] =
            "No connection to the provider — check the internet.",
        ["Ключ отклонён сервером — проверьте, что скопирован верный API-ключ."] =
            "The key was rejected by the server — make sure the correct API key was copied.",
        ["Слишком много запросов к API — попробуйте позже."] = "Too many API requests — try again later.",
        ["Провайдер вернул ошибку (HTTP {0})."] = "The provider returned an error (HTTP {0}).",
        ["Ключ больше не действителен — переподключите API-ключ в разделе AI."] =
            "The key is no longer valid — reconnect the API key in the AI section.",
        ["Недостаточно средств на балансе аккаунта провайдера."] =
            "Insufficient balance in the provider account.",
        ["Лимит бесплатных запросов исчерпан — попробуйте завтра или пополните баланс OpenRouter."] =
            "The free request limit is exhausted — try again tomorrow or top up your OpenRouter balance.",
        ["Бесплатный лимит Neurons на сегодня исчерпан — попробуйте завтра."] =
            "The free daily Neurons allowance is exhausted — try again tomorrow.",
        ["Токен отклонён Cloudflare — проверьте токен и разрешения Workers AI."] =
            "The token was rejected by Cloudflare — check the token and its Workers AI permission.",
        ["Cloudflare не принял запрос — проверьте Account ID аккаунта."] =
            "Cloudflare rejected the request — check the account Account ID.",
        ["Модель ушла в размышления и не успела дать ответ — попробуйте повторить вопрос."] =
            "The model spent its whole budget thinking and gave no answer — try repeating the question.",
        ["Время ожидания ответа истекло."] = "The response timed out.",

        // ===== SCU AI Assistant (окно помощника, карточки, tools) =====
        ["Я AI-помощник SCU. Я могу помочь с функциями, настройками, диагностикой и внутренней справкой SCU. Этот вопрос вне моей области — спросите лучше о разделе или настройке SCU."] =
            "I am the SCU AI assistant. I can help with SCU features, settings, diagnostics and the built-in help. This question is outside my scope — ask me about an SCU section or setting instead.",
        ["Полный режим: ответы по справке SCU, чтение состояния, навигация и изменение настроек с подтверждением."] =
            "Full mode: answers from the SCU help, reading state, navigation and changing settings with your confirmation.",
        ["Этот провайдер не поддерживает вызов функций: ответы по справке и текстовые подсказки. Изменения настроек недоступны."] =
            "This provider does not support function calling: help-based answers and text hints only. Changing settings is unavailable.",
        ["Сбой запроса к AI: {0}"] = "AI request failed: {0}",
        ["Не удалось применить изменение: {0}"] = "Failed to apply the change: {0}",
        ["Применение отменено."] = "Applying was cancelled.",
        ["Изменение отменено — настройки не затронуты."] = "The change was cancelled — settings are untouched.",
        ["Нашёл в справке SCU:"] = "Found in the SCU help:",
        ["Применено: {0}"] = "Applied: {0}",
        ["Ошибка «{0}»: {1}"] = "Error in «{0}»: {1}",
        ["Изменение не найдено или истекло — подготовьте его заново."] =
            "The change was not found or has expired — prepare it again.",
        ["План изменения не найден или истёк. Подготовьте его заново."] =
            "The change plan was not found or has expired. Prepare it again.",
        ["Изменение не подтверждено пользователем."] = "The change has not been confirmed by the user.",
        ["Функция SCU больше не зарегистрирована: {0}"] = "The SCU utility is no longer registered: {0}",
        ["Состояние изменилось после подготовки preview. Подготовьте изменение заново."] =
            "The state changed after the preview was prepared. Prepare the change again.",
        ["Подтвердите опасное изменение"] = "Confirm the dangerous change",
        ["через точку восстановления или резервную копию раздела"] =
            "via a restore point or a section backup",
        ["Опасная операция не подтверждена — система не изменилась."] =
            "The dangerous operation was not confirmed — the system is unchanged.",
        ["Операция сейчас недоступна — раздел занят или нет прав администратора."] =
            "The operation is unavailable right now — the section is busy or administrator rights are missing.",
        ["Неизвестный id функции SCU: {0}"] = "Unknown SCU utility id: {0}",
        ["Функция уже находится в запрошенном состоянии — менять нечего."] =
            "The utility is already in the requested state — nothing to change.",
        ["Изменение: {0} — {1} → {2}."] = "Change: {0} — {1} → {2}.",
        ["Функция SCU «{0}» будет применена через существующую команду раздела {1}."] =
            "The SCU utility «{0}» will be applied via the existing command of section {1}.",
        ["Неизвестное желаемое состояние: {0}. Допустимы только «on» и «off»."] =
            "Unknown desired state: {0}. Only «on» and «off» are allowed.",
        ["Функция SCU «{0}» применяется только в состоянии «{1}» — противоположное направление недоступно."] =
            "The SCU utility «{0}» applies only to the state «{1}» — the opposite direction is unavailable.",
        ["Неизвестный tool: {0}"] = "Unknown tool: {0}",
        ["Эта модель не поддерживает вызов функций — изменение настроек недоступно."] =
            "This model does not support function calling — changing settings is unavailable.",
        ["Невалидные аргументы tool: {0}"] = "Invalid tool arguments: {0}",
        ["Внутренняя ошибка выполнения tool."] = "Internal error while executing the tool.",
        ["Запись справки не найдена: {0}"] = "Help entry not found: {0}",
        ["Не указан номер раздела."] = "Section number is not specified.",
        ["Раздел {0} не существует в SCU."] = "Section {0} does not exist in SCU.",
        ["Не удалось получить информацию о системе."] = "Failed to get system information.",
        ["Диагностика уже выполняется — дождитесь окончания."] =
            "Diagnostics are already running — wait for them to finish.",
        ["Применить операцию"] = "Run the operation",
        ["Защита включена"] = "Protection enabled",
        ["Защита отключена"] = "Protection disabled",
        ["Можно применить"] = "Can be applied",
        ["Применено"] = "Applied",
        ["Операция"] = "Operation",

        // ===== SCU AI Assistant: чтение файлов приложения =====
        ["Путь должен быть относительным и указывать внутрь папки установки SCU."] =
            "The path must be relative and point inside the SCU installation folder.",
        ["Файлы вне папки установки SCU недоступны."] =
            "Files outside the SCU installation folder are not accessible.",
        ["Папка не найдена: {0}"] = "Folder not found: {0}",
        ["Не удалось прочитать содержимое папки."] = "Failed to read the folder contents.",
        ["Не указан путь к файлу."] = "No file path was specified.",
        ["Файл не найден: {0}"] = "File not found: {0}",
        ["Чтение файлов этого типа не поддерживается: {0}"] =
            "Reading files of this type is not supported: {0}",
        ["Файл слишком большой для чтения ({0} байт)."] =
            "The file is too large to read ({0} bytes).",
        ["Не удалось прочитать файл."] = "Failed to read the file.",

        // ===== Размеры (вкладка «Приложения») =====
        ["{0:0} МБ"] = "{0:0} MB",
        ["{0:0} КБ"] = "{0:0} KB",
        ["< 1 КБ"] = "< 1 KB",

        // ===== Сортировка (вкладка «Приложения») =====
        ["По имени"] = "By name",
        ["По размеру"] = "By size",
        ["По дате установки"] = "By install date",
        ["По возрастанию"] = "Ascending",
        ["По убыванию"] = "Descending",
    };


    // === StatusEn ===
}
