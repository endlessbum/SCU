using System.Text.Json;
using SCU.Common;

namespace SCU.Infrastructure.Storage;

// Список утилит, включённых в пакетный запуск большого выключателя раздела
// «Состояние ПК»: %AppData%\SCU\state\batch.json. Один файл — один список
// идентификаторов; та же стратегия записи (temp + move) и восстановления после
// повреждения, что у JsonStateFile. Ошибки чтения/записи не роняют вызывающий код.
public sealed class BatchStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Logger _logger;
    private readonly string _path;

    public BatchStateStore(Logger logger, string? filePath = null)
    {
        _logger = logger;
        _path = filePath ?? DefaultPath();
    }

    public static string DefaultPath() =>
        Path.Combine(DashboardStatePaths.Directory, "batch.json");

    // Чтение best-effort: нет файла или повреждён — возвращается пустой список
    // (тогда действуют дефолтные включения, заданные реестром утилит).
    // null — файла ещё нет (применяется дефолтный набор); [] — валидный выбор
    // пользователя «выключить всё», который не должен подменяться дефолтом.
    public List<string>? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (Exception exception)
        {
            try { File.Delete(_path + ".tmp"); } catch { } // п. №15 аудита
            _logger.Warn("BATCH | state file corrupted: " + exception.Message);
            return null;
        }
    }

    public void Save(IReadOnlyList<string> includedIds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(includedIds, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception exception)
        {
            // П. №15 аудита: остаток .tmp после сбоя — мусор и ложный
            // «незавершённый файл» при следующем чтении.
            try { File.Delete(_path + ".tmp"); } catch { /* .tmp мог не создаться */ }
            _logger.Warn("BATCH | save failed | " + exception.Message);
        }
    }
}
