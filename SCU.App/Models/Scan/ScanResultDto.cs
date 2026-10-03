using System.Text.Json.Serialization;

namespace SCU.Models.Scan;

// DTO событий и итогового отчёта ScannerCore.exe (документ п. 4/44).
// Протокол: построчный JSON в stdout ScannerCore, кодировка UTF-8.
// Имена полей совпадают с C++-структурами; парсинг — JsonSerializer с
// PropertyNameCaseInsensitive, поэтому атрибуты нужны только для
// snake_case-полей (их в протоколе нет — все camelCase).

public enum ScanVerdict
{
    Clean,
    Suspicious,
    Malware,
    Error,
}

public sealed class DetectionDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "clean";

    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("signedFile")]
    public bool SignedFile { get; set; }

    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public int Score { get; set; }

    // Для члена архива: контейнер на диске. Карантин по умолчанию изолирует
    // исходный архив целиком (документ п. 20).
    [JsonPropertyName("containerPath")]
    public string ContainerPath { get; set; } = string.Empty;

    [JsonPropertyName("isVirtual")]
    public bool IsVirtual { get; set; }

    // Источник обнаружения: "file" | "persistence" | "process" (документ п. 17/18).
    [JsonPropertyName("source")]
    public string Source { get; set; } = "file";

    // Реальный путь для карантина: контейнер архива или сам файл.
    [JsonIgnore]
    public string QuarantineTarget => !string.IsNullOrEmpty(ContainerPath) ? ContainerPath : Path;

    [JsonPropertyName("signals")]
    public List<string> Signals { get; set; } = new();
}

public sealed class ScanSummaryDto
{
    [JsonPropertyName("filesScanned")]
    public long FilesScanned { get; set; }

    [JsonPropertyName("filesSkipped")]
    public long FilesSkipped { get; set; }

    [JsonPropertyName("errors")]
    public long Errors { get; set; }

    [JsonPropertyName("detections")]
    public long Detections { get; set; }
}

// Итоговое событие finished — единственный источник результата скана.
public sealed class ScanResultDto
{
    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; set; }

    [JsonPropertyName("summary")]
    public ScanSummaryDto Summary { get; set; } = new();

    [JsonPropertyName("detections")]
    public List<DetectionDto> Detections { get; set; } = new();

    // Классификация исхода по матрице аудита: не приходит из протокола,
    // проставляется ScannerRunner'ом сразу после разбора finished.
    [JsonIgnore]
    public ScanOutcome Outcome { get; set; } = ScanOutcome.Clean;
}

public enum ScanEventKind
{
    Started,
    Progress,
    Detection,
    Warning,
    Error,
    Update,
    Finished,
    Unknown,
}

// Разобранная строка stdout ScannerCore. Только Kind заполнен для unknown-событий:
// вперед совместимость — новые события старого GUI не роняют (документ п. 39).
public sealed class ScanEvent
{
    public ScanEventKind Kind { get; set; } = ScanEventKind.Unknown;

    public string EngineVersion { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string DbVersion { get; set; } = string.Empty;
    public string DbDate { get; set; } = string.Empty;

    public long Scanned { get; set; }
    public long Skipped { get; set; }
    public long Detections { get; set; }
    public string Current { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    // Update event: статус установки пакета базы (п. 30).
    public string UpdateStatus { get; set; } = string.Empty;
    public long UpdateEntries { get; set; }

    public DetectionDto? Detection { get; set; }
    public ScanResultDto? Result { get; set; }
}
