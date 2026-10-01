using SCU.Common;

namespace SCU.AppCore;

// П.2: retention бэкапов. Каталоги %AppData%\SCU\backup\* (tasks, services
// и другие) с временными именами файлов (tasks_*.txt, services_*.txt) растут бесконечно.
// Правило: в каталоге хранятся MaxFilesPerDirectory самых свежих файлов; более старые
// удаляются при создании нового (и контрольным обходом на старте приложения).
// Файловые логи (Logger) и history.json имеют собственные лимиты — сюда не входят.
public static class BackupRetention
{
    public const int MaxFilesPerDirectory = 10;

    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup");

    // Оставить MaxFilesPerDirectory самых свежих файлов каталога, остальные удалить.
    // Любой сбой (нет каталога, файл занят) не роняет вызывающую операцию: бэкап
    // важнее уборки. Сортировка по LastWriteTimeUtc, при равенстве — по имени (стабильно).
    public static void Enforce(string? directory, Logger? logger = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var stale = Directory.EnumerateFiles(directory)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxFilesPerDirectory)
                .ToList();

            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                    logger?.Info("RETENTION | deleted old backup | " + file.FullName);
                }
                catch (Exception exception)
                {
                    // Файл мог быть занят другим процессом — пропускаем, попробуют в следующий раз.
                    logger?.Warn("RETENTION | delete failed | " + file.FullName + " | " + exception.Message);
                }
            }
        }
        catch (Exception exception)
        {
            logger?.Warn("RETENTION | enforce failed | " + directory + " | " + exception.Message);
        }
    }

    // Контрольный обход всех подкаталогов backup-корня (страховка от накопления,
    // оставшегося от прежних версий приложения).
    public static void EnforceAll(string? rootDirectory = null, Logger? logger = null)
    {
        try
        {
            var root = rootDirectory ?? BackupRoot;
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                Enforce(directory, logger);
            }
        }
        catch (Exception exception)
        {
            logger?.Warn("RETENTION | enforce all failed | " + exception.Message);
        }
    }
}
