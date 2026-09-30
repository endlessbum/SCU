namespace SCU.Infrastructure.Windows.Troubleshooting;

public sealed record DiagnosticDiskSnapshot(
    string Letter,
    ulong TotalBytes,
    ulong FreeBytes,
    bool IsSystem,
    string FileSystem);

public sealed record EventErrorSummary(
    string Provider,
    int EventId,
    string LogName,
    int Count,
    DateTime? LatestTime);

public sealed record StartupEntrySnapshot(string Name, string Command, string Source, string? Problem);

public sealed record ServiceSnapshot(string Name, string DisplayName, bool IsStartAllowed, bool IsRunning);

/// <summary>Устройство с ненулевым ConfigManagerErrorCode (п. 7.8 плана).</summary>
public sealed record DriverProblemSnapshot(string Name, string DeviceId, string Manufacturer, uint ProblemCode);

/// <summary>Диск, для которого SMART предсказывает отказ (PredictFailure = true).</summary>
public sealed record SmartFailureSnapshot(string DriveName);

/// <summary>Звуковое устройство: имя, статус устройства и problem code (если есть).</summary>
public sealed record AudioDeviceSnapshot(string Name, string Manufacturer, string Status, uint ProblemCode);

/// <summary>
/// Собранные данные одного диагностического запуска. Probes заполняют свои блоки;
/// упавший probe оставляет блок пустым (null) — правила по нему не срабатывают.
/// </summary>
public sealed class DiagnosticContext
{
    public string? OsCaption { get; set; }
    public TimeSpan? Uptime { get; set; }
    public ulong? RamTotalBytes { get; set; }
    public ulong? RamAvailableBytes { get; set; }
    public bool? PendingReboot { get; set; }
    public string? PendingRebootSource { get; set; }

    public IReadOnlyList<DiagnosticDiskSnapshot> Disks { get; set; } = [];

    /// <summary>Состояния критичных служб (включая службы обновления).</summary>
    public IReadOnlyList<ServiceSnapshot> Services { get; set; } = [];

    public bool? UpdatePaused { get; set; }
    public bool? UpdateBlocked { get; set; }
    public string? UpdatePauseInfo { get; set; }

    public bool? NetworkHasActiveAdapter { get; set; }
    public bool? NetworkHasGateway { get; set; }
    public string? NetworkGateway { get; set; }
    public bool? NetworkDnsResolves { get; set; }
    public string? NetworkDnsTarget { get; set; }

    public bool? UacStandard { get; set; }
    public bool? AntivirusRegistered { get; set; }
    public bool? AntivirusRealTimeOn { get; set; }

    public IReadOnlyList<EventErrorSummary> EventErrors { get; set; } = [];
    public bool EventLogReadFailed { get; set; }

    public IReadOnlyList<StartupEntrySnapshot> StartupEntries { get; set; } = [];

    public IReadOnlyList<DriverProblemSnapshot> DriverProblems { get; set; } = [];

    public IReadOnlyList<SmartFailureSnapshot> SmartFailures { get; set; } = [];

    /// <summary>Все присутствующие звуковые устройства (Win32_SoundDevice).</summary>
    public IReadOnlyList<AudioDeviceSnapshot> AudioDevices { get; set; } = [];

    /// <summary>null — проверить хранилище компонентов не удалось; false — повреждений нет.</summary>
    public bool? ComponentStoreCorrupted { get; set; }
    public string? ComponentStoreSample { get; set; }
}
