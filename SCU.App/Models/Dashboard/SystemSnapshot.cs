namespace SCU.Models;

// Состояние диска в снимке: числовые величины (в отличие от строковых DiskInfo из
// SystemInfoService) — нужны правилам рекомендаций. null — прочитать не удалось.
public sealed record DiskSnapshot(
    string Letter,
    double TotalGb,
    double FreeGb,
    double? UsedPercent);

// Краткая сетевая сводка. Значения — «как есть» из netsh/реестра; null — чтение не удалось.
public sealed record NetworkSummarySnapshot(
    string? AutoTuning,
    string? Ecn,
    string? Qos,
    string? NetBios);

// Снимок состояния ПК для раздела «Состояние ПК» (Dashboard).
// Собираются только реально прочитанные данные: отсутствующее поле остаётся null —
// никаких вычисленных/выдуманных значений. Свойства изменяемые: SystemStateService
// наполняет снимок по областям, а DashboardViewModel перекрывает лёгкие поля свежими.
// Сериализуется System.Text.Json (SnapshotStore).
public sealed class SystemSnapshot
{
    public DateTime Timestamp { get; set; }

    public string? AppVersion { get; set; }

    public string? WindowsVersion { get; set; }

    public string? WindowsBuild { get; set; }

    public string? Cpu { get; set; }

    public string? Ram { get; set; }

    public string? Gpu { get; set; }

    public IReadOnlyList<DiskSnapshot> Disks { get; set; } = [];

    public int? StartupCount { get; set; }

    // Список автозагрузки (StartupList) не различает включённые и отключённые
    // элементы — поле остаётся null до появления такого источника (этапы 2–3).
    public int? DisabledCount { get; set; }

    public int? ServicesOk { get; set; }

    public int? ServicesChanged { get; set; }

    // Службы контрольного набора, отсутствующие в системе (NotFound): после
    // деблоата это осознанное состояние — бэнчмарк не считает их нечитаемыми.
    public int? ServicesMissing { get; set; }

    public int? ServicesTotal { get; set; }

    public int? TasksTotal { get; set; }

    public int? TasksDisabled { get; set; }

    public NetworkSummarySnapshot? Network { get; set; }

    public string? ActivePlanGuid { get; set; }

    public bool? UpdateBlocked { get; set; }

    public bool? UpdatePaused { get; set; }

    // Нейтральное значение ("standard"/"weakened"); локализация — на слое отображения.
    public string? UacState { get; set; }

    public int? PrivacyAppliedCount { get; set; }

    public int? PrivacyTotal { get; set; }

    // Область → сообщение об ошибке чтения. Пустой словарь — все области прочитаны.
    public Dictionary<string, string> AreaErrors { get; set; } = [];
}
