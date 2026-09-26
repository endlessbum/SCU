namespace SCU.Models;

/// <summary>
/// Эффективное состояние системной настройки Windows.
/// Не сводить к bool: Unknown / PendingReboot / Mixed / Unavailable обязательны.
/// </summary>
public enum SystemSettingState
{
    /// <summary>Функция включена и реально применяется.</summary>
    Enabled,

    /// <summary>Функция отключена и реально применяется.</summary>
    Disabled,

    /// <summary>Не удалось достоверно определить состояние.</summary>
    Unknown,

    /// <summary>Изменение записано, но Windows ещё работает со старым состоянием (нужна перезагрузка).</summary>
    PendingReboot,

    /// <summary>Частично включено (например, не все компоненты).</summary>
    PartiallyEnabled,

    /// <summary>Разные тома/экземпляры имеют разное состояние.</summary>
    Mixed,

    /// <summary>Функция недоступна на этой системе / редакции / железе.</summary>
    Unavailable
}

/// <summary>
/// Runtime-состояние службы Windows (ServiceControllerStatus).
/// Не путать с Startup Type (тип запуска).
/// </summary>
public enum ServiceRuntimeStatus
{
    Running,
    Stopped,
    StartPending,
    StopPending,
    ContinuePending,
    PausePending,
    Paused,
    Unknown
}

/// <summary>
/// Тип запуска службы (ServiceStartMode + DelayedAutostart).
/// </summary>
public enum ServiceStartupType
{
    Boot,
    System,
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
    Unknown
}

/// <summary>
/// Результат операции над системным параметром.
/// «Команда завершилась» ≠ «настройка применена».
/// </summary>
public enum OperationOutcome
{
    Success,
    Applied,
    PendingReboot,
    Failed,
    AccessDenied,
    Timeout,
    NotFound,
    Partial,
    Unknown
}

/// <summary>
/// Единый результат операции с проверкой фактического состояния.
/// </summary>
public sealed class OperationResult
{
    public OperationOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool RequiresReboot { get; init; }
    public int Code { get; init; }

    public bool IsSuccess => Outcome is OperationOutcome.Success or OperationOutcome.Applied or OperationOutcome.PendingReboot;

    public static OperationResult Ok(string message = "", bool requiresReboot = false) =>
        new()
        {
            Outcome = requiresReboot ? OperationOutcome.PendingReboot : OperationOutcome.Applied,
            Message = message,
            RequiresReboot = requiresReboot
        };

    public static OperationResult Fail(string message, OperationOutcome outcome = OperationOutcome.Failed, int code = 1) =>
        new() { Outcome = outcome, Message = message, Code = code };

    public static OperationResult AccessDenied(string message = "Нет доступа") =>
        new() { Outcome = OperationOutcome.AccessDenied, Message = message, Code = 5 };

    public static OperationResult TimedOut(string message = "Таймаут ожидания") =>
        new() { Outcome = OperationOutcome.Timeout, Message = message, Code = -2 };

    public static OperationResult NotFound(string message = "Не найдено") =>
        new() { Outcome = OperationOutcome.NotFound, Message = message, Code = 2 };
}

/// <summary>Тип файла гибернации Windows.</summary>
public enum HiberfileType
{
    /// <summary>Файл отсутствует.</summary>
    None,
    /// <summary>Полный hiberfil.sys (полная гибернация).</summary>
    Full,
    /// <summary>Уменьшенный файл только для Fast Startup.</summary>
    Reduced,
    Unknown
}

/// <summary>
/// Фактическое состояние Fast Startup (не только HiberbootEnabled).
/// </summary>
public sealed record FastStartupInfo(
    SystemSettingState State,
    int? HiberbootEnabled,
    HiberfileType HiberfileType,
    bool HybridSleepAvailable,
    bool HibernateAvailable,
    bool RequiresReboot);

/// <summary>
/// Глобальный режим имён 8.3 (NtfsDisable8dot3NameCreation).
/// 0 = enabled for all, 1 = disabled for all, 2 = per-volume, 3 = disabled except system volume.
/// </summary>
public enum ShortNameGlobalMode
{
    EnabledForAll = 0,
    DisabledForAll = 1,
    PerVolume = 2,
    DisabledExceptSystem = 3,
    Unknown = -1
}

/// <summary>Состояние 8.3: глобальный режим + при необходимости per-volume.</summary>
public sealed record ShortNamesInfo(
    SystemSettingState EffectiveState,
    ShortNameGlobalMode GlobalMode,
    IReadOnlyList<(string Volume, bool Enabled)>? VolumeStates);

/// <summary>Состояние Last Access (disablelastaccess / NtfsDisableLastAccessUpdate).</summary>
public sealed record LastAccessInfo(
    SystemSettingState State,
    int? RawValue,
    bool RequiresReboot);

/// <summary>Один файл подкачки.</summary>
public sealed record PageFileEntry(
    string Path,
    int? InitialSizeMb,
    int? MaximumSizeMb);

/// <summary>
/// Фактическое состояние pagefile: System Managed vs Custom, список файлов.
/// </summary>
public sealed record PageFileInfo(
    bool SystemManaged,
    IReadOnlyList<PageFileEntry> Entries,
    /// <summary>Crash dump требует pagefile (Complete/Kernel dump).</summary>
    bool CrashDumpMayRequirePagefile,
    string? CrashDumpSummary);

/// <summary>Конфигурация дампа памяти (реестр CrashControl).</summary>
public sealed record CrashDumpInfo(
    int CrashDumpEnabled,
    string? DumpFile,
    bool MayRequirePagefile,
    string Summary);
