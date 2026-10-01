namespace SCU.Common;

// П. 13 аудита: чистые данные переводов, вынесены из фасада L (Localization.cs).
// Внутренняя деталь фасада: вызывать напрямую нельзя — только через L.T/L.S.
internal static partial class LocalizationDictionaries
{

    // === En ===

    // Английские переводы строк статуса, п. 13 аудита: чистые данные по зонам.
    internal static readonly Dictionary<string, string> StatusEn = new()
    {
        ["Отменено"] = "Cancelled",
        ["Диски не выбраны."] = "No drives selected.",
        ["Индексация содержимого отключена на выбранных дисках (проверено чтением атрибутов)."] =
            "Content indexing was disabled on the selected drives (verified by reading attributes).",
        ["Индексация содержимого включена на выбранных дисках (проверено чтением атрибутов)."] =
            "Content indexing was enabled on the selected drives (verified by reading attributes).",
        ["DISM и SFC выполнены успешно (оба кода 0)."] = "DISM and SFC completed successfully (both exit codes were 0).",
        ["Кэш Delivery Optimization очищен."] = "Delivery Optimization cache cleaned.",
        ["Бэкап TCP Global отсутствует — восстанавливать нечего."] = "No TCP Global backup exists — nothing to restore.",
        ["Бэкап TCP Global повреждён."] = "The TCP Global backup is corrupted.",
        ["TCP Global не восстановлены; бэкап сохранён."] = "TCP Global was not restored; the backup is preserved.",
        ["TCP Global не подтверждены после восстановления; бэкап сохранён."] = "TCP Global could not be verified after restore; the backup is preserved.",
        ["MTU должен быть в диапазоне 576..1500."] = "MTU must be in the 576..1500 range.",
        ["Бэкап MTU отсутствует — восстанавливать нечего."] = "No MTU backup exists — nothing to restore.",
        ["MTU восстановлен не полностью; бэкап сохранён."] = "MTU was not fully restored; the backup is preserved.",
        ["MTU восстановлены из бэкапа."] = "MTU values restored from the backup.",
        ["QoS override не применился."] = "The QoS override was not applied.",
        ["Бэкап QoS отсутствует — восстанавливать нечего."] = "No QoS backup exists — nothing to restore.",
        ["Бэкап свойств адаптеров отсутствует — восстанавливать нечего."] = "No adapter properties backup exists — nothing to restore.",
        ["Бэкап QoS повреждён: ожидается absent или present=<0..100>."] =
            "The QoS backup is corrupted: expected absent or present=<0..100>.",
        ["Режим NetBIOS должен быть 0, 1 или 2."] = "The NetBIOS mode must be 0, 1 or 2.",
        ["NetBIOS: один или несколько интерфейсов не изменены."] = "NetBIOS: one or more interfaces were not changed.",
        ["NetBIOS: проверка после изменения не прошла."] = "NetBIOS: verification after the change failed.",
        ["Бэкап NetBIOS отсутствует — восстанавливать нечего."] = "No NetBIOS backup exists — nothing to restore.",
        ["NetBIOS не восстановлен; бэкап сохранён."] = "NetBIOS was not restored; the backup is preserved.",
        ["Бэкап NetBIOS пуст — восстанавливать нечего (адаптеры не найдены)."] =
            "The NetBIOS backup is empty — nothing to restore (no adapters were found).",
        ["NetBIOS восстановлен из бэкапа."] = "NetBIOS restored from the backup.",
        ["Кэш NetBIOS сброшен."] = "NetBIOS cache flushed.",
        ["Сброс кэша NetBIOS выполнен не полностью."] = "NetBIOS cache flush did not complete fully.",
        ["Кэш DNS очищен."] = "DNS cache flushed.",
        ["Не удалось очистить кэш DNS."] = "Failed to flush the DNS cache.",
        ["Сертификат Минцифры не найден в хранилищах сертификатов."] =
            "The Mincifry certificate was not found in the certificate stores.",
        ["Не удалось открыть хранилища сертификатов: "] = "Failed to open the certificate stores: ",
        ["Не удалось сохранить исходный TCP Global — профиль отменён."] =
            "Failed to save the original TCP Global settings — profile cancelled.",
        ["Игровой профиль применён (Auto-Tuning=disabled, ECN=disabled, QoS=0%)."] =
            "Gaming profile applied (Auto-Tuning=disabled, ECN=disabled, QoS=0%).",
        ["Сброс завершён: TCP Global, QoS, MTU и NetBIOS восстановлены."] =
            "Reset completed: TCP Global, QoS, MTU and NetBIOS were restored.",
        ["Бэкап уже существует."] = "Backup already exists.",
        ["Бэкап сохранён."] = "Backup saved.",
        ["Все параметры применены и проверены."] = "All settings were applied and verified.",
        ["Реестр восстановлен из бэкапа."] = "Registry restored from the backup.",
        ["Файл бэкапа реестра не найден."] = "Registry backup file not found.",
        ["Параметры не применились: "] = "Settings were not applied: ",
        ["Службы обновления отключены"] = "update services are disabled",
        ["Службы обновления возвращены к исходному состоянию."] =
            "Update services have been restored to their original state.",
        ["Ускорение мыши включено."] = "Mouse acceleration enabled.",
        ["Ускорение мыши отключено."] = "Mouse acceleration disabled.",
        ["Ускорение запуска Edge возвращено к поведению по умолчанию."] =
            "Edge startup acceleration restored to the default behavior.",
        ["Game Bar и фоновая запись отключены. Game Mode оставлен включённым."] =
            "Game Bar and background recording are disabled. Game Mode remains enabled.",
        ["Точка восстановления создана."] = "Restore point created.",
        ["Компонент уже установлен."] = "Component is already installed.",
        ["Проверка подписи не пройдена: "] = "Signature verification failed: ",
        ["Подпись подтверждена."] = "Signature verified.",
        ["Не удалось запустить установщик."] = "Failed to launch the installer.",
        ["Скачано "] = "Downloaded ",
        ["Операция завершена."] = "Operation completed.",
        ["План электропитания установлен и подтверждён."] = "Power plan set and verified.",
        ["Гибернация включена."] = "Hibernation enabled.",
        ["Гибернация отключена."] = "Hibernation disabled.",
        ["Быстрый запуск отключён."] = "Fast Startup disabled.",
        ["Быстрый запуск включён."] = "Fast Startup enabled.",
        ["Сжатие памяти включено."] = "Memory compression enabled.",
        ["Сжатие памяти отключено."] = "Memory compression disabled.",
        ["Не удалось переключить сжатие памяти"] = "Failed to toggle memory compression",
        ["Создание имён 8.3 включено (перезагрузка не требуется для новых файлов)."] =
            "8.3 name creation enabled (no reboot is required for new files).",
        ["Создание имён 8.3 отключено (полностью применится после перезагрузки)."] =
            "8.3 name creation disabled (fully applied after reboot).",
        ["Учёт времени последнего доступа включён."] = "Last-access timestamp updates enabled.",
        ["Учёт времени последнего доступа отключён (перезагрузка не требуется)."] =
            "Last-access timestamp updates disabled (no reboot required).",
        ["Prefetcher включён."] = "Prefetcher enabled.",
        ["Prefetcher отключён."] = "Prefetcher disabled.",
        ["Размер файла подкачки должен быть в диапазоне 16..262144 МБ."] =
            "Page file size must be in the 16..262144 MB range.",
        ["Не удалось открыть раздел реестра для настройки файла подкачки."] =
            "Failed to open the registry key for page file configuration.",
        ["Настройка файла подкачки не подтвердилась чтением реестра."] =
            "Page file configuration was not verified by reading the registry.",
        ["Бэкап файла подкачки не был создан."] = "The page file backup was not created.",
        ["Не удалось открыть раздел реестра для бэкапирования файла подкачки."] =
            "Failed to open the registry key for backing up the page file settings.",
        ["BCD: ограничения не сняты после удаления."] = "BCD limits were not removed after deletion.",
        ["Ограничения CPU/ОЗУ в BCD отсутствуют (проверено чтением)."] =
            "No CPU/RAM limits remain in the BCD (verified by reading).",
        ["Схемы электропитания не найдены."] = "No power plans found.",
        ["Драйверы снова устанавливаются через Windows Update."] =
            "Drivers are installed through Windows Update again.",
        ["Авто-установка драйверов через Windows Update запрещена."] =
            "Automatic driver installation through Windows Update is disabled.",
        ["Не удалось сохранить исходный MTU — изменение отменено."] =
            "Failed to save the original MTU — change cancelled.",
        ["Нет файла бэкапа для отката."] = "No backup file to restore from.",
        ["Задача отключена. "] = "Task disabled. ",
        ["Бэкап задач уже существует; задачи уже отключены."] =
            "The task backup already exists; the tasks are already disabled.",
        ["Бэкап задач CEIP не создавался — включение недоступно."] =
            "The CEIP task backup was not created — enabling is unavailable.",
        ["Задержка меню должна быть в диапазоне 0..1000 мс."] =
            "Menu delay must be in the 0..1000 ms range.",
        ["Раздел «Рекомендуем» показан."] = "The «Recommended» section is shown.",
        ["Проводник перезапущен."] = "Explorer restarted.",
        ["Проводник не перезапустился — запустите его вручную."] =
            "Explorer did not restart — start it manually.",
        ["Бэкап панели задач не найден — сначала выполните очистку из текущего профиля."] =
            "Taskbar backup not found — run cleanup from the current profile first.",
        ["Бэкап панели задач пуст."] = "Taskbar backup is empty.",
        ["Не найден SCU.ps1"] = "SCU.ps1 was not found",
        ["Телеметрия и реклама"] = "Telemetry and advertising",
        ["Уведомления и советы"] = "Notifications and tips",
        ["Фоновые UWP-приложения"] = "Background UWP apps",
        ["Windows Copilot AI"] = "Windows Copilot AI",
        ["Оптимизация доставки"] = "Delivery Optimization",
        ["Планировщик: телеметрия / CEIP"] = "Task Scheduler: telemetry / CEIP",
        ["TEMP пользователя"] = "User TEMP",
        ["кэш загрузок обновлений"] = "Windows Update download cache",
        ["Проводник открывает «Этот компьютер»"] = "Explorer opens «This PC»",
        ["Кнопка «Главная» в навигации"] = "«Home» button in navigation",
        ["Кнопка «Галерея» в навигации"] = "«Gallery» button in navigation",
        ["Кнопка «Сеть» в навигации"] = "«Network» button in navigation",
        ["Корзина в навигации"] = "Recycle Bin in navigation",
        ["Корзина на рабочем столе"] = "Recycle Bin on the desktop",
        ["Компактный вид проводника"] = "Compact Explorer view",
        ["Недавние файлы"] = "Recent files",
        ["Классическое контекстное меню"] = "Classic context menu",
        ["Расширения файлов"] = "File name extensions",
        ["Скрытые и системные файлы"] = "Hidden and system files",
        ["Полный путь в заголовке"] = "Full path in the title bar",
        ["Флажки элементов"] = "Item check boxes",
        ["OneDrive в навигации"] = "OneDrive in navigation",
        ["Анимации окон и меню"] = "Window and menu animations",
        ["Прозрачность Windows"] = "Windows transparency",
        ["Эскизы вместо значков"] = "Thumbnails instead of icons",
        ["Тени и полупрозрачное выделение"] = "Shadows and translucent selection",
        ["Раздел «Рекомендуем» в меню Пуск"] = "«Recommended» section in Start",
        ["Камера"] = "Camera",
        ["Центр разработки"] = "Dev Home",
        ["Центр отзывов"] = "Feedback Hub",
        ["Поиск Microsoft Bing"] = "Microsoft Bing Search",
        ["Новости Microsoft"] = "Microsoft News",
        ["Outlook (новый)"] = "Outlook (new)",
        ["Быстрая помощь"] = "Quick Assist",
        ["Косынка"] = "Solitaire",
        ["Звукозапись"] = "Sound Recorder",
        ["Записки"] = "Sticky Notes",
        ["Камера Windows"] = "Windows Camera",
    };


    // === StatusTemplates ===
}
