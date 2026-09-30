namespace SCU.Common;

// П. 13 аудита: чистые данные переводов, вынесены из фасада L (Localization.cs).
// Внутренняя деталь фасада: вызывать напрямую нельзя — только через L.T/L.S.
internal static partial class LocalizationDictionaries
{

    // === En ===

    // Фрагменты статуса, п. 13 аудита: чистые данные по зонам.
    internal static readonly Dictionary<string, string> StatusFragments = new()
    {
        ["SCU.ps1 не найден."] = "SCU.ps1 was not found.",
        ["Не задан Action."] = "Action is not specified.",
        ["Не удалось запустить PowerShell."] = "Failed to start PowerShell.",
        ["Не удалось запустить SCU.ps1: "] = "Failed to start SCU.ps1: ",
        ["Ошибка ожидания SCU.ps1: "] = "Error while waiting for SCU.ps1: ",
        ["SCU выполнен успешно."] = "SCU completed successfully.",
        ["частичная ошибка"] = "partial error",
        ["не найден входной файл"] = "input file not found",
        ["фатальная ошибка скрипта"] = "fatal script error",
        ["Отменено"] = "Cancelled",
        ["Система не запустила повышенный процесс."] = "The system did not start the elevated process.",
        ["Запрошен перезапуск с правами администратора."] = "Restart with administrator rights was requested.",
        ["Повышение прав отменено пользователем."] = "Elevation was cancelled by the user.",
        ["Не удалось перезапустить приложение: "] = "Failed to restart the application: ",
        ["Не удалось определить путь SCU.exe. Соберите проект и запускайте exe, а не хост dotnet."] =
            "Could not determine the SCU.exe path. Build the project and run the exe, not the dotnet host.",
        ["Уже запущено с правами администратора."] = "Already running with administrator rights.",
        ["Не все диски удалось обработать: "] = "Not all disks could be processed: ",
        ["Проверка целостности завершилась с замечаниями: "] = "Integrity check completed with warnings: ",
        ["DISM завершился с кодом "] = "DISM exited with code ",
        ["Не удалось очистить кэш Delivery Optimization (код "] =
            "Failed to clear the Delivery Optimization cache (code ",
        ["compact завершился с кодом "] = "compact exited with code ",
        ["Не удалось подготовить защищённый каталог загрузок: "] =
            "Failed to prepare the protected download directory: ",
        ["Проверка подписи не пройдена: "] = "Signature verification failed: ",
        ["Проверка подписи перед запуском не пройдена: "] =
            "Signature verification before launch failed: ",
        ["Не удалось запустить установщик: "] = "Failed to start the installer: ",
        ["Установщик находится по reparse-point/символьной ссылке — запуск заблокирован."] =
            "The installer is a reparse point/symbolic link — launch blocked.",
        ["Не задано имя службы."] = "Service name is not specified.",
        ["Не удалось сохранить исходный TCP Global — изменение отменено."] =
            "Failed to save the original TCP Global settings — change cancelled.",
        ["Изменение применено, но не подтверждено чтением: "] =
            "Change applied, but not verified by reading: ",
        ["TCP Global не подтверждены после восстановления; резерв сохранён."] =
            "TCP Global settings were not verified after restore; backup preserved.",
        ["MTU изменён, но не подтверждён чтением: "] = "MTU changed, but not verified by reading: ",
        ["Не удалось сохранить исходный MTU — изменение отменено."] =
            "Failed to save the original MTU — change cancelled.",
        ["Не удалось сохранить исходные TCP Global параметры — изменение отменено."] =
            "Failed to save the original TCP Global settings — change cancelled.",
        ["Режим NetBIOS должен быть 0, 1 или 2."] = "NetBIOS mode must be 0, 1, or 2.",
        ["NetBIOS: один или несколько интерфейсов не изменены."] =
            "NetBIOS: one or more interfaces were not changed.",
        ["NetBIOS: проверка после изменения не прошла."] =
            "NetBIOS: verification after the change failed.",
        ["Не удалось сохранить TCP Global — профиль отменён."] =
            "Failed to save TCP Global — profile cancelled.",
        ["Не удалось выгрузить текущие свойства адаптеров: "] =
            "Failed to dump the current adapter properties: ",
        ["Не удалось сохранить исходный QoS: "] = "Failed to save the original QoS: ",
        ["Не удалось изменить QoS: "] = "Failed to change QoS: ",
        ["Не удалось записать резерв MTU: "] = "Failed to save the MTU backup: ",
        ["Не удалось записать резерв TCP Global: "] = "Failed to save the TCP Global backup: ",
        ["Профиль применён частично: "] = "Profile applied partially: ",
        ["Сброс завершён не полностью: "] = "Reset did not complete fully: ",
        ["Не удалось прочитать текущий MTU: "] = "Failed to read the current MTU: ",
        ["Не удалось сохранить/установить QoS — профиль отменён: "] =
            "Failed to save/apply QoS — profile cancelled: ",
        ["Для «"] = "For «",
        ["Контекстное меню: "] = "Context menu: ",
        ["«Рекомендуем»: "] = "«Recommended»: ",
        ["Перезапуск проводника: "] = "Explorer restart: ",
        ["Очистка панели задач: "] = "Taskbar cleanup: ",
        ["Не удалось получить текущий профиль: "] = "Failed to get the current profile: ",
        ["Не все параметры Game Bar / DVR применены: "] = "Not all Game Bar / DVR settings were applied: ",
        ["Категория ceip требует SCURunner (не передан в конструктор)."] =
            "The ceip category requires SCURunner (not provided to the constructor).",
        ["Параметры применены и проверены. "] = "Settings applied and verified. ",
        ["Включено. "] = "Enabled. ",
        ["Не удалось сохранить резерв реестра: "] = "Failed to save the registry backup: ",
        ["Резерв реестра повреждён: "] = "The registry backup is corrupted: ",
        ["Не удалось восстановить реестр: "] = "Failed to restore the registry: ",
        ["Не удалось удалить "] = "Failed to delete ",
        ["Не удалось открыть "] = "Failed to open ",
        ["Не удалось прочитать "] = "Failed to read ",
        ["AppX/Edge: повторная проверка подтверждает отсутствие."] =
            "AppX/Edge: follow-up verification confirms absence.",
        ["Edge setup.exe не прошёл проверку подписи Microsoft — удаление отменено. "] =
            "Edge setup.exe failed Microsoft signature verification — removal cancelled. ",
        ["Edge: setup.exe завершился с кодом "] = "Edge: setup.exe exited with code ",
        ["Edge: после удаления остались файлы или пакеты; см. лог."] =
            "Edge: files or packages remain after removal; see the log.",
        ["Edge: удаление подтверждено проверкой файлов и AppX."] =
            "Edge: removal confirmed by file and AppX verification.",
        ["WMI SystemRestore не вернул код результата."] =
            "WMI SystemRestore did not return a result code.",
        ["WinVerifyTrust недоступен: "] = "WinVerifyTrust is unavailable: ",
        ["Xbox: удаляемые пакеты отсутствуют (проверено скриптом). Системный компонент XboxGameCallableUI остаётся в системе."] =
            "Xbox: removable packages are absent (verified by script). The system XboxGameCallableUI component remains.",
        ["powercfg завершился с кодом "] = "powercfg exited with code ",
        ["powercfg не вернул GUID нового плана."] = "powercfg did not return the new plan GUID.",
        ["Защита системы отключена. Включите её в «Параметрах → О системе → Защита системы» или средствами этого раздела."] =
            "System protection is disabled. Enable it in Settings → System → System protection, or by using this section.",
        ["Массовое удаление завершилось не полностью: "] =
            "Bulk removal did not complete fully: ",
        ["Не восстановлено ни одного значения: "] = "No values were restored: ",
        ["Не удалось настроить файл подкачки: "] = "Failed to configure the page file: ",
        ["Не удалось переключить сжатие памяти (код "] =
            "Failed to toggle memory compression (code ",
        ["Не удалось повторно проверить BCD: "] = "Failed to verify the BCD again: ",
        ["Не удалось прочитать BCD: "] = "Failed to read the BCD: ",
        ["Не удалось прочитать список планов: "] = "Failed to read the power plan list: ",
        ["Не удалось создать копию плана: "] = "Failed to duplicate the power plan: ",
        ["Не удалось создать точку восстановления: "] = "Failed to create the restore point: ",
        ["Не удалось сохранить резерв файла подкачки: "] =
            "Failed to save the page file backup: ",
        ["Пауза обновлений должна быть от 1 до 35 дней."] =
            "The update pause must be between 1 and 35 days.",
        ["План активирован, но не удалось подтвердить его чтением: "] =
            "The power plan was activated, but could not be verified by reading: ",
        ["Провайдер wintrust недоступен."] = "The WinTrust provider is unavailable.",
        ["Реестр обновлён, но SystemParametersInfo не применил параметры."] =
            "The registry was updated, but SystemParametersInfo did not apply the settings.",
        ["Резерв панели задач не читается: "] = "The taskbar backup cannot be read: ",
        ["Файл не имеет подписи Authenticode."] = "The file has no Authenticode signature.",
        ["Файл не найден: "] = "File not found: ",
        ["Файл не подписан или подпись не читается: "] =
            "The file is not signed or the signature cannot be read: ",
        ["Хэш файла не совпадает с подписью — файл изменён."] =
            "The file hash does not match the signature — the file was modified.",
        ["Проверка подписи "] = "Verifying signature ",
        ["Очистка: "] = "Cleaning: ",
    };


}
