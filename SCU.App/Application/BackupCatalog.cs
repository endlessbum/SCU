using SCU.Common;

namespace SCU.AppCore;

// Одна запись каталога бэкапов: раздел-владелец, назначение, файл и время.
public sealed record BackupEntry(
    string Section,
    string Purpose,
    string FileName,
    DateTime Created,
    string FullPath);

// Каталог всех бэкапов приложения: %AppData%\SCU\backup\<подкаталог>\*.
// Каждый подкаталог заведует своим разделом (набор см. BackupRetention и
// создателей бэкапов: services/tasks/power/bcd/network/privacy/security/
// input/ui/update/startup). Неизвестные подкаталоги тоже показываются —
// с нейтральной подписью, чтобы окно не молчало о чужих файлах.
public static class BackupCatalog
{
    private static readonly Dictionary<string, (string Section, string Purpose)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tasks"] = ("Задачи планировщика", "Состояния задач планировщика (отключённые задачи) для отката"),
            ["services"] = ("Службы Windows", "Исходные типы запуска служб для отката"),
            ["startup"] = ("Автозагрузка", "Манифест записей автозагрузки для восстановления"),
            ["power"] = ("Питание, память и CPU", "Схемы и параметры PowerCfg, файл подкачки, твики файловой системы"),
            ["bcd"] = ("Питание, память и CPU", "Исходные значения ограничений CPU и ОЗУ (bcdedit)"),
            ["network"] = ("Сеть", "Параметры TCP, MTU, NetBIOS и QoS до изменений"),
            ["privacy"] = ("Приватность и телеметрия", "Состояния служб и задач телеметрии до изменений"),
            ["security"] = ("Безопасность", "Исходные значения параметров безопасности (UAC)"),
            ["input"] = ("Ввод, браузер и игры", "Исходные значения параметров ввода"),
            ["ui"] = ("Интерфейс", "Исходные значения параметров интерфейса"),
            ["update"] = ("Обновления Windows", "Состояния служб обновлений до блокировки"),
        };

    private const string UnknownSection = "SCU";
    private const string UnknownPurpose = "Резервная копия SCU";

    // Свежие сверху; сбои чтения отдельного каталога не роняют перечисление.
    public static IReadOnlyList<BackupEntry> Collect()
    {
        var root = BackupRetention.BackupRoot;
        var result = new List<BackupEntry>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var dirName = Path.GetFileName(directory);
            var (section, purpose) = Known.TryGetValue(dirName, out var known)
                ? known
                : (UnknownSection, UnknownPurpose);

            string[] files;
            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    result.Add(new BackupEntry(
                        section,
                        purpose,
                        Path.GetFileName(file),
                        File.GetLastWriteTime(file),
                        file));
                }
                catch
                {
                    // Файл мог исчезнуть между перечислением и чтением свойств.
                }
            }
        }

        return result.OrderByDescending(entry => entry.Created).ToList();
    }
}
