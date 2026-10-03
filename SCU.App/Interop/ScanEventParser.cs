using System.Text.Json;
using SCU.Models.Scan;

namespace SCU.Interop;

// Парсер построчных JSON-событий ScannerCore.exe (документ п. 39).
// Не-JSON строки (маловероятно: CRT-вывод, сбои) не роняют скан — Kind=Unknown,
// а раннер пишет их в лог. Вынесен в static-класс для тестов (SCU.Tests).
public static class ScanEventParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ScanEvent Parse(string line)
    {
        var scanEvent = new ScanEvent();
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return scanEvent;
        }

        try
        {
            var document = JsonDocument.Parse(line);
            using var parsed = document;
            if (!document.RootElement.TryGetProperty("event", out var kindElement)
                || kindElement.ValueKind != JsonValueKind.String)
            {
                return scanEvent;
            }

            var root = document.RootElement;
            switch (kindElement.GetString())
            {
                case "started":
                    scanEvent.Kind = ScanEventKind.Started;
                    scanEvent.EngineVersion = GetString(root, "engineVersion");
                    scanEvent.Mode = GetString(root, "mode");
                    scanEvent.DbVersion = GetString(root, "dbVersion");
                    scanEvent.DbDate = GetString(root, "dbDate");
                    break;

                case "progress":
                    scanEvent.Kind = ScanEventKind.Progress;
                    scanEvent.Scanned = GetInt64(root, "scanned");
                    scanEvent.Skipped = GetInt64(root, "skipped");
                    scanEvent.Detections = GetInt64(root, "detections");
                    scanEvent.Current = GetString(root, "current");
                    break;

                case "detection":
                    scanEvent.Kind = ScanEventKind.Detection;
                    scanEvent.Detection = root.Deserialize<DetectionDto>(Options);
                    break;

                case "warning":
                    scanEvent.Kind = ScanEventKind.Warning;
                    scanEvent.Message = GetString(root, "message");
                    break;

                case "error":
                    scanEvent.Kind = ScanEventKind.Error;
                    scanEvent.Message = GetString(root, "message");
                    break;

                case "update":
                    scanEvent.Kind = ScanEventKind.Update;
                    scanEvent.UpdateStatus = GetString(root, "status");
                    scanEvent.Message = GetString(root, "error");
                    scanEvent.DbVersion = GetString(root, "dbVersion");
                    scanEvent.UpdateEntries = GetInt64(root, "entries");
                    break;

                case "finished":
                    scanEvent.Kind = ScanEventKind.Finished;
                    // finished без payload результата ({"event":"finished"}, обрыв
                    // протокола) — НЕ «пустой чистый скан» (аудит 3, п. 2): Result
                    // остаётся null, раннер обязан классифицировать прогон как Failed.
                    scanEvent.Result = root.TryGetProperty("summary", out _)
                        ? root.Deserialize<ScanResultDto>(Options)
                        : null;
                    break;

                default:
                    // Новые события будущих версий ScannerCore: совместимо пропускаем.
                    scanEvent.Kind = ScanEventKind.Unknown;
                    break;
            }
        }
        catch (JsonException)
        {
            scanEvent.Kind = ScanEventKind.Unknown;
        }

        return scanEvent;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long GetInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : 0;
}
