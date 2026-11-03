using SCU.Common;

namespace SCU.AppCore;

// П.2: retention бэкапов. Каталоги %AppData%\SCU\backup\* (tasks, services
// и другие) с временными именами файлов (tasks_*.txt, services_*.txt) растут бесконечно.
// Правило: в каталоге хранятся MaxFilesPerDirectory самых свежих файлов; более старые
// удаляются при создании нового (и контрольным обходом на старте приложения).
// Файловые логи (Logger) и history.json имеют собственные лимиты — сюда не входят.
// Исключение — каталог startup (аудит: P1, потеря файлов пользователя): его
// file_*.bak — ПЕРЕМЕЩЁННЫЕ файлы автозагрузки, Move-Item сохраняет их исходный
// (обычно старый) LastWriteTime, поэтому mtime-based retention удалял бы самые
// нужные бэкапы первыми, а manifest.json продолжал бы на них ссылаться —
// восстановление элемента становилось невозможным навсегда. Жизненный цикл
// startup-бэкапов управляется манифестом, а не счётчиком свежести.
public static class BackupRetention
{
    public const int MaxFilesPerDirectory = 10;
    private const string RetentionExemptDirectory = "startup";

    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup");

    // Оставить MaxFilesPerDirectory самых свежих файлов каталога, остальные удалить.
    // Любой сбой (нет каталога, файл занят) не роняет вызывающую операцию: бэкап
    // важнее уборки. Сортировка по LastWriteTimeUtc, при равенстве — по имени (стабильно).
    // rootDirectory — если задан, каталог обязан лежать внутри него (канонический путь).
    public static void Enforce(string? directory, Logger? logger = null, string? rootDirectory = null)
    {
        if (IsRetentionExempt(directory, logger))
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            // Аудит, п. 2: junction/reparse point внутри backup-дерева увёл бы
            // удаление файлов в целевой каталог (при запуске с правами администратора —
            // за пределы %AppData%\SCU\backup).
            if (!IsSafeForRetention(directory, rootDirectory, logger))
            {
                return;
            }

            var stale = Directory.EnumerateFiles(directory)
                .Select(path => new FileInfo(path))
                // Ссылки (reparse point) не считаем и не удаляем: их адресат — чужой
                // файл, а mtime ссылки не отражает свежесть бэкапа.
                .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
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
                if (IsRetentionExempt(directory, logger))
                {
                    continue;
                }

                Enforce(directory, logger, root);
            }
        }
        catch (Exception exception)
        {
            logger?.Warn("RETENTION | enforce all failed | " + exception.Message);
        }
    }

    // Каталог пригоден для retention: лежит внутри канонического корня (если он
    // задан), и ни он сам, ни кто-то из его родителей не является reparse point.
    // Так уборка не может уйти по junction/symlink в чужой каталог.
    private static bool IsSafeForRetention(string directory, string? rootDirectory, Logger? logger)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(directory);
        }
        catch (Exception exception)
        {
            logger?.Warn("RETENTION | path resolve failed | " + directory + " | " + exception.Message);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rootDirectory))
        {
            var canonicalRoot = CanonicalPath(rootDirectory);
            var canonicalDirectory = CanonicalPath(fullPath);
            if (!canonicalDirectory.StartsWith(
                    canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                logger?.Warn("RETENTION | outside backup root | " + fullPath);
                return false;
            }
        }

        for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
        {
            try
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null)
                {
                    logger?.Warn("RETENTION | skipped reparse point | " + current.FullName);
                    return false;
                }
            }
            catch (Exception exception)
            {
                // Не смогли убедиться, что каталог обычный, — уборку не выполняем.
                logger?.Warn("RETENTION | attributes failed | " + current.FullName + " | " + exception.Message);
                return false;
            }
        }

        return true;
    }

    private static string CanonicalPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsRetentionExempt(string? directory, Logger? logger)
    {
        if (directory is null)
        {
            return false;
        }

        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(name, RetentionExemptDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        logger?.Info("RETENTION | skipped | startup backups are managed by manifest.json");
        return true;
    }
}
