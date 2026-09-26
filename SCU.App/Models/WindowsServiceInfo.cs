namespace SCU.Models;

/// <summary>
/// Полная информация о службе Windows.
/// Startup Type и Runtime Status — разные сущности и не смешиваются в один bool/enum.
/// </summary>
public sealed record WindowsServiceInfo(
    string Name,
    string DisplayName,
    ServiceStartupType StartupType,
    ServiceRuntimeStatus RuntimeStatus,
    bool DelayedAutostart,
    /// <summary>
    /// Можно ли службе запускаться по конфигурации (не Disabled).
    /// ON = StartupType != Disabled, OFF = Disabled.
    /// </summary>
    bool IsStartAllowed,
    ServiceQueryStatus QueryStatus = ServiceQueryStatus.Ok)
{
    /// <summary>
    /// Совместимость со старым кодом: приблизительное «состояние» для UI, который ещё не обновлён.
    /// Предпочтительно использовать StartupType + RuntimeStatus напрямую.
    /// </summary>
    public WindowsServiceState LegacyState
    {
        get
        {
            if (QueryStatus == ServiceQueryStatus.NotFound)
                return WindowsServiceState.Missing;
            if (QueryStatus == ServiceQueryStatus.AccessDenied)
                return WindowsServiceState.AccessDenied;
            if (QueryStatus != ServiceQueryStatus.Ok)
                return WindowsServiceState.Unknown;
            if (StartupType == ServiceStartupType.Disabled)
                return WindowsServiceState.Disabled;
            return RuntimeStatus == ServiceRuntimeStatus.Running
                ? WindowsServiceState.Running
                : WindowsServiceState.Stopped;
        }
    }
}

/// <summary>
/// Результат запроса службы (не путать с runtime status).
/// AccessDenied и NotFound — разные вещи.
/// </summary>
public enum ServiceQueryStatus
{
    Ok,
    NotFound,
    AccessDenied,
    Timeout,
    Error,
    Unknown
}

/// <summary>
/// Устаревший enum для постепенной миграции UI.
/// Новый код должен использовать ServiceStartupType + ServiceRuntimeStatus + ServiceQueryStatus.
/// </summary>
public enum WindowsServiceState
{
    Running,
    Stopped,
    Disabled,
    Missing,
    AccessDenied,
    Unknown
}
