namespace SCU.Models.Drivers;

// Устройство с установленным драйвером (ключевое оборудование).
public sealed record DriverDeviceInfo(
    string Name,
    string Provider,
    string Version,
    DateTime? DriverDate,
    string DeviceClass,
    bool HasProblem,
    uint ProblemCode);

// Проблемное устройство: код ошибки конфигурации > 0 (Win32_PnPEntity).
public sealed record DriverProblemInfo(
    string Name,
    uint ProblemCode,
    string Manufacturer,
    string DeviceId);

// Результат инвентаризации: устройства и проблемные (для карточек раздела).
public sealed record DriverInventory(
    IReadOnlyList<DriverDeviceInfo> Devices,
    IReadOnlyList<DriverProblemInfo> Problems);

// Доступное обновление драйвера из Windows Update (результат онлайн-поиска).
// Key — идентификатор UpdateID:Revision для выбора пакета в установщике.
public sealed record DriverUpdateInfo(
    string Key,
    string Title,
    string Provider,
    string Version,
    DateTime? DriverDate,
    long SizeBytes);

// Итог установки пакета(ов) драйверов через WU Installer.
public sealed record DriverInstallSummary(
    int Failed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> SucceededKeys,
    bool RebootRequired);
