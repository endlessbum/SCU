using System.Text.Json;
using SCU.Common;

namespace SCU.Infrastructure.Storage;

// Список закреплённых на главной карточек: %AppData%\SCU\state\pinned-cards.json.
// Один файл — один список идентификаторов карточек; та же стратегия записи
// (temp + move) и восстановления после повреждения, что у BatchStateStore.
// Ошибки чтения/записи не роняют вызывающий код.
public sealed class PinnedCardsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Logger _logger;
    private readonly string _path;

    public PinnedCardsStore(Logger logger, string? filePath = null)
    {
        _logger = logger;
        _path = filePath ?? DefaultPath();
    }

    public static string DefaultPath() =>
        Path.Combine(DashboardStatePaths.Directory, "pinned-cards.json");

    // Чтение best-effort: нет файла или повреждён — пустой список (ничего не закреплено).
    public List<string> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (Exception exception)
        {
            try { File.Delete(_path + ".tmp"); } catch { } // п. №15 аудита
            _logger.Warn("PINCARDS | state file corrupted: " + exception.Message);
            return [];
        }
    }

    public void Save(IReadOnlyList<string> pinnedIds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(pinnedIds, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception exception)
        {
            // П. №15 аудита: остаток .tmp после сбоя — мусор и ложный
            // «незавершённый файл» при следующем чтении.
            try { File.Delete(_path + ".tmp"); } catch { /* .tmp мог не создаться */ }
            _logger.Warn("PINCARDS | save failed | " + exception.Message);
        }
    }
}
