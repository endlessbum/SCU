using SCU.Infrastructure.Logging;

namespace SCU.Infrastructure.Parsing;

// П. 13 аудита: чтение бэкапа служб (services_*.txt) — чистый парсер файла,
// вынесен из ServiceManager.
// П. №17 аудита: «прочесть не удалось» теперь отличимо в логе от «бэкапа нет» —
// прежде тихий catch возвращал null в обоих случаях.
internal static class ServiceBackupParser
{
    private static readonly Logger Log = Logger.CurrentRun;

    internal static string? FindLatestServicesBackupPath()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCU", "backup", "services");
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(directory, "services_*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception)
        {
            Log.Warn("SVC | backup directory unreadable | " + exception.Message);
            return null;
        }
    }

    // Start из строки бэкапа "name|start|state|delayed"; бэкап повреждён/нет записи — Manual.
    internal static int? ReadStartFromBackup(string? backupFilePath, string serviceName)    {
        try
        {
            if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(backupFilePath))
            {
                var parts = line.Split('|');
                if (parts.Length >= 2 && string.Equals(parts[0].Trim(), serviceName, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[1].Trim(), out var start))
                {
                    return start;
                }
            }
        }
        catch (Exception exception)
        {
            // Бэкап — только подсказка для включения службы: ошибка чтения не
            // ломает операцию, но отличить «повреждён» от «нет записи» важно (п. №17).
            Log.Warn("SVC | backup read failed | " + Path.GetFileName(backupFilePath ?? string.Empty) + " | " + exception.Message);
        }

        return null;
    }
}
