using CommunityToolkit.Mvvm.ComponentModel;
using SCU.Common;
using SCU.Models;

namespace SCU.ViewModels.Sections;

public partial class ServiceRowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>Legacy-состояние для совместимости; предпочтительно StartupType + RuntimeStatus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(ActionCaption))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(IsServiceEnabled))]
    [NotifyPropertyChangedFor(nameof(ShouldDisable))]
    private WindowsServiceState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    [NotifyPropertyChangedFor(nameof(StartupTypeText))]
    [NotifyPropertyChangedFor(nameof(IsServiceEnabled))]
    [NotifyPropertyChangedFor(nameof(ShouldDisable))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private ServiceStartupType _startupType = ServiceStartupType.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    [NotifyPropertyChangedFor(nameof(RuntimeStatusText))]
    private ServiceRuntimeStatus _runtimeStatus = ServiceRuntimeStatus.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    [NotifyPropertyChangedFor(nameof(QueryStatusText))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(IsServiceEnabled))]
    private ServiceQueryStatus _queryStatus = ServiceQueryStatus.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    [NotifyPropertyChangedFor(nameof(DelayedAutostartText))]
    private bool _delayedAutostart;

    /// <summary>Runtime status localized (Работает / Остановлена / Запускается…).</summary>
    public string RuntimeStatusText => RuntimeStatus switch
    {
        ServiceRuntimeStatus.Running => L.T("Работает"),
        ServiceRuntimeStatus.Stopped => L.T("Остановлена"),
        ServiceRuntimeStatus.StartPending => L.T("Запускается…"),
        ServiceRuntimeStatus.StopPending => L.T("Останавливается…"),
        ServiceRuntimeStatus.ContinuePending => L.T("Возобновляется…"),
        ServiceRuntimeStatus.PausePending => L.T("Приостанавливается…"),
        ServiceRuntimeStatus.Paused => L.T("Приостановлена"),
        _ => L.T("Неизвестно")
    };

    /// <summary>Startup type localized.</summary>
    public string StartupTypeText => StartupType switch
    {
        ServiceStartupType.Boot => L.T("Загрузка"),
        ServiceStartupType.System => L.T("Система"),
        ServiceStartupType.Automatic => L.T("Автоматически"),
        ServiceStartupType.AutomaticDelayed => L.T("Автоматически (отложенный)"),
        ServiceStartupType.Manual => L.T("Вручную"),
        ServiceStartupType.Disabled => L.T("Отключена"),
        _ => L.T("Неизвестно")
    };

    public string QueryStatusText => QueryStatus switch
    {
        ServiceQueryStatus.Ok => string.Empty,
        ServiceQueryStatus.NotFound => L.T("Служба отсутствует"),
        ServiceQueryStatus.AccessDenied => L.T("Нет доступа"),
        ServiceQueryStatus.Timeout => L.T("Таймаут"),
        ServiceQueryStatus.Error => L.T("Ошибка запроса"),
        _ => L.T("Состояние не удалось определить")
    };

    /// <summary>
    /// Текст основного статуса для строки: runtime, либо причина невозможности чтения.
    /// </summary>
    public string StateText =>
        QueryStatus == ServiceQueryStatus.Ok
            ? RuntimeStatusText
            : QueryStatusText;

    public string DelayedAutostartText => DelayedAutostart ? L.T("да") : L.T("нет");

    public string ToolTipText =>
        QueryStatus == ServiceQueryStatus.Ok
            ? L.T("Служба: {0}\nТип запуска: {1}\nСостояние: {2}\nОтложенный запуск: {3}",
                Name, StartupTypeText, RuntimeStatusText, DelayedAutostartText)
            : L.T("Служба: {0}\n{1}\nНе удалось получить достоверное состояние Windows.\nПроверьте права администратора или повторите проверку.",
                Name, QueryStatusText);

    /// <summary>Тумблер доступен только если служба реально прочитана.</summary>
    public bool CanToggle => QueryStatus == ServiceQueryStatus.Ok;

    /// <summary>
    /// Подпись действия больше не «Включить/Отключить» как постоянный текст.
    /// Для совместимости со старым UI: краткий статус конфигурации.
    /// </summary>
    public string ActionCaption => QueryStatus switch
    {
        ServiceQueryStatus.NotFound => L.T("Нет службы"),
        ServiceQueryStatus.AccessDenied => L.T("Нет доступа"),
        ServiceQueryStatus.Ok when StartupType == ServiceStartupType.Disabled => L.T("Запуск запрещён"),
        ServiceQueryStatus.Ok => L.T("Запуск разрешён"),
        _ => L.T("Неизвестно")
    };

    /// <summary>
    /// Переключатель = разрешён ли запуск (Startup Type != Disabled).
    /// Runtime status рядом показывается отдельно и не влияет на тумблер.
    /// </summary>
    public bool IsServiceEnabled =>
        QueryStatus == ServiceQueryStatus.Ok && StartupType != ServiceStartupType.Disabled;

    public bool ShouldDisable => IsServiceEnabled;

    /// <summary>
    /// Постоянное описание функции (не зависит от ON/OFF).
    /// </summary>
    public string ActionText
    {
        get
        {
            if (ServiceDescriptions.TryGetValue(Name, out var text))
                return L.T(text);
            if (QueryStatus == ServiceQueryStatus.Ok)
                return L.T("Описание для этой службы отсутствует. Показаны только сведения, определённые Windows.");
            return string.Empty;
        }
    }

    private static readonly Dictionary<string, string> ServiceDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SysMain"] =
            "Предзагрузка часто используемых программ в ОЗУ (Superfetch/SysMain). Отключение снижает фоновую нагрузку на диск и CPU — обычно делают на SSD и на слабых ПК. На HDD с одними и теми же программами лучше оставлять включённой. Не обещает прирост FPS.",
        ["DiagTrack"] =
            "Connected User Experiences and Telemetry — сбор и отправка телеметрии Microsoft. На работу системы почти не влияет; отключают для уменьшения фонового трафика. Работа «Программы улучшения качества ПО» прекратится.",
        ["dmwappushservice"] =
            "Канал push-сообщений WAP и часть корпоративной телеметрии. На домашнем ПК обычно не нужна; может потребоваться корпоративным средствам управления (Intune, WAP).",
        ["WSearch"] =
            "Служба индексирования поиска Windows. Отвечает за индексирование файлов, писем и другого содержимого для быстрого поиска. Отключение уменьшает фоновую активность и запись на диск, но может сделать поиск медленнее. Обычно имеет смысл оставлять включённой на обычных рабочих ПК.",
        ["Fax"] =
            "Отправка и приём факсов. Если факсами не пользуетесь — отключение безопасно и не влияет на остальную систему.",
        ["XblAuthManager"] =
            "Авторизация в Xbox Live. Отключайте, если не играете в игры Xbox и Game Pass — вход в них перестанет работать.",
        ["XblGameSave"] =
            "Синхронизация облачных сохранений игр Xbox. Отключайте только если не играете в игры из Store/Game Pass: локальные сохранения останутся, облачная синхронизация прекратится.",
        ["XboxGipSvc"] =
            "Сетевые компоненты Xbox (сопоставление игроков, чаты). Не нужна без игр Xbox; на остальную сеть не влияет.",
        ["XboxNetApiSvc"] =
            "Сетевой интерфейс Xbox Live. Можно отключить без игр Xbox — на обычную сеть и интернет не влияет.",
        ["RemoteRegistry"] =
            "Удалённое редактирование реестра другими ПК. На домашнем ПК обычно не используется — отключение повышает безопасность без побочных эффектов для локальной работы.",
        ["RemoteAccess"] =
            "Маршрутизация и удалённый доступ (RRAS). Не нужна, если не пользуетесь входящими VPN-подключениями Windows.",
        ["WbioSrvc"] =
            "Биометрия Windows Hello — вход по отпечатку или лицу. Отключение отключает биометрический вход; пароль продолжает работать.",
        ["TabletInputService"] =
            "Сенсорная клавиатура, перо и рукописный ввод. Нужна на планшетах и сенсорных экранах; на обычном десктопе можно отключить.",
        ["MapsBroker"] =
            "Скачивание и обновление офлайн-карт. Если Картами Windows не пользуетесь — можно отключать без последствий.",
        ["RetailDemo"] =
            "Демо-режим для витрин магазинов. На домашнем ПК бесполезна — безопасно отключать.",
        ["wisvc"] =
            "Получение тестовых сборок Windows Insider. Не участвуете в программе предварительной оценки — можно отключать.",
        ["WerSvc"] =
            "Отчёты об ошибках Windows (Windows Error Reporting). Отключение прекращает отправку данных о сбоях; поиск причин BSOD по коду может затрудниться.",
        ["PcaSvc"] =
            "Помощник совместимости программ (отслеживает проблемы запуска старых приложений). В целом безопасно оставить; отключают, если мешает работе старых программ.",
        ["PrintNotify"] =
            "Уведомления очереди печати. Нужна только при работе с принтерами; без принтера можно отключить.",
        ["Spooler"] =
            "Диспетчер печати (Print Spooler). Отключение останавливает печать и установку части программ, использующих spooler. Отключайте только если принтерами не пользуетесь совсем."
    };

    public void ForceStateNotify()
    {
        OnPropertyChanged(nameof(IsServiceEnabled));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StartupTypeText));
        OnPropertyChanged(nameof(RuntimeStatusText));
        OnPropertyChanged(nameof(CanToggle));
    }

    public static ServiceRowViewModel From(WindowsServiceInfo info)
    {
        var vm = new ServiceRowViewModel();
        vm.Apply(info);
        return vm;
    }

    public void Apply(WindowsServiceInfo info)
    {
        Name = info.Name;
        DisplayName = info.DisplayName;
        StartupType = info.StartupType;
        RuntimeStatus = info.RuntimeStatus;
        DelayedAutostart = info.DelayedAutostart;
        QueryStatus = info.QueryStatus;
        State = info.LegacyState;
        ForceStateNotify();
    }
}
