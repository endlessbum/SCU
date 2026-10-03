using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SCU.Infrastructure.Logging;

namespace SCU.Infrastructure.Storage;

// Версия последней установленной базы сканера (%APPDATA%\SCU\scanner-db-state.json).
// Автообновление сравнивает её с версией ассета db-version.json на релизе
// scanner-db и пропускает скачивание пакета, если база уже актуальна.
// Отсутствующий или повреждённый файл — «неизвестно»: пакет скачивается, как раньше.
internal sealed class ScannerDbStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Logger? _logger;

    public ScannerDbStateStore(Logger? logger, string path)
    {
        _logger = logger;
        _path = path;
    }

    // Календарная версия (yyyy.MM.dd) последней установленной базы; null — неизвестна.
    public string? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(_path), JsonOptions);
            return string.IsNullOrWhiteSpace(state?.Version) ? null : state.Version;
        }
        catch (Exception exception)
        {
            _logger?.Warn("SCANNER DB STATE | load failed | " + exception.Message);
            return null;
        }
    }

    public void Save(string version)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(new StateDto { Version = version }, JsonOptions));
        }
        catch (Exception exception)
        {
            // Не запомнили версию — следующая проверка просто перекачает пакет.
            _logger?.Warn("SCANNER DB STATE | save failed | " + exception.Message);
        }
    }

    private sealed class StateDto
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
