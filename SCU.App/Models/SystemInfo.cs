namespace SCU.Models;

public sealed record InfoRow(string Label, string Value);

public sealed record DiskInfo(
    string Letter,
    string VolumeLabel,
    string FileSystem,
    string Total,
    string Free,
    string Used,
    string UsedPercent)
{
    // Числовые величины для машинной обработки (бэнчмарк, диффы): строки Total/Free —
    // только для отображения, парсить их обратно хрупко (формат и единицы могут меняться).
    public ulong TotalBytes { get; init; }

    public ulong FreeBytes { get; init; }
}

public sealed class SystemInfo
{
    public IReadOnlyList<InfoRow> Fields { get; init; } = [];

    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];

    public IReadOnlyList<string> VideoAdapters { get; init; } = [];

    public IReadOnlyList<string> NetworkAdapters { get; init; } = [];

    // Типизированные значения полей, которые читает SystemStateService: подписи
    // («ОС», «Процессор»…) живут только для отображения, искать их по строке-метке хрупко.
    public string? OsCaption { get; init; }

    public string? OsVersion { get; init; }

    public string? CpuSummary { get; init; }

    public ulong? RamTotalBytes { get; init; }
}
