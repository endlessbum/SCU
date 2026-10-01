namespace SCU.Models;

using SCU.Common;

public sealed record ScheduledTaskInfo(
    string FullPath,
    string State)
{
    public string Name
    {
        get
        {
            var i = FullPath.LastIndexOf('\\');
            return i < 0 ? FullPath : FullPath[(i + 1)..];
        }
    }

    public string Folder
    {
        get
        {
            var i = FullPath.LastIndexOf('\\');
            return i <= 0 ? "\\" : FullPath[..i];
        }
    }

    // Информер карточки: только полный путь (состояние видно справа в строке).
    public string ToolTipText => L.T("Полный путь: {0}", FullPath);

    // Назначение задачи для подписи под названием. Список задач фиксированный
    // (TaskManager.DefaultTaskPaths), ключ — имя задачи без папки.
    public string Purpose => PurposeByName.TryGetValue(Name, out var text) ? L.T(text) : string.Empty;

    private static readonly Dictionary<string, string> PurposeByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft Compatibility Appraiser"] =
            "Собирает данные о программах и драйверах для телеметрии совместимости Windows.",
        ["ProgramDataUpdater"] =
            "Собирает телеметрию о файлах и программах для оценки совместимости обновлений.",
        ["StartupAppTask"] =
            "Следит за приложениями автозагрузки и показывает уведомления об их влиянии на запуск.",
        ["Proxy"] =
            "Собирает данные Autochk после внезапного выключения ПК.",
        ["Consolidator"] =
            "Отправляет данные программы улучшения качества Windows (CEIP).",
        ["UsbCeip"] =
            "Отправляет данные об использовании USB-устройств в рамках CEIP.",
        ["Microsoft-Windows-DiskDiagnosticDataCollector"] =
            "Собирает диагностические данные о дисках для отчётов Windows.",
        ["DmClient"] =
            "Отправляет журналы использования и данные обратной связи Windows.",
        ["DmClientOnScenarioDownload"] =
            "Отправляет данные обратной связи Windows при скачивании сценариев.",
        ["MapsToastTask"] =
            "Показывает всплывающие уведомления Карт Windows.",
        ["MapsUpdateTask"] =
            "Проверяет и загружает обновления офлайн-карт.",
        ["FamilySafetyMonitor"] =
            "Проверяет настройки семейной безопасности (родительский контроль).",
        ["FamilySafetyRefreshTask"] =
            "Синхронизирует настройки семейной безопасности с аккаунтом Microsoft.",
        ["QueueReporting"] =
            "Отправляет накопленные отчёты об ошибках Windows (WER).",
        ["XblGameSaveTask"] =
            "Синхронизирует облачные сохранения игр Xbox Live."
    };

    // Экранное состояние: перевод L.T поверх сырых строк парсера ("Ready"/"MISSING" и т.п.).
    // Логика (IsMissing/IsDisabled) продолжает читать сырой State.
    public string LocalizedState
    {
        get
        {
            if (IsMissing)
            {
                return L.T("Нет задачи");
            }

            return State.Trim().ToUpperInvariant() switch
            {
                "READY" => L.T("Готова"),
                "RUNNING" => L.T("Выполняется"),
                "DISABLED" => L.T("Отключена"),
                _ => State
            };
        }
    }

    public bool IsMissing => string.Equals(State, "MISSING", StringComparison.OrdinalIgnoreCase);

    public bool IsDisabled => string.Equals(State, "Disabled", StringComparison.OrdinalIgnoreCase);

    public bool CanDisable => !IsMissing && !IsDisabled;

    // П.18: «Нет задачи» на кнопке не показывается — у отсутствующей задачи та же
    // кнопка «Отключить», но неактивная (CanDisable=false → команда недоступна).
    public string ActionCaption => IsDisabled
        ? L.T("Отключена")
        : L.T("Отключить");
}
