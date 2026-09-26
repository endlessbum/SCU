namespace SCU.Services;

// П. 13 аудита: чтение резерва служб (services_*.txt) — чистый парсер файла,
// вынесен из ServiceManager.
internal static class ServiceBackupParser
{
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
        catch
        {
            return null;
        }
    }

    // Start из строки резерва "name|start|state|delayed"; резерв повреждён/нет записи — Manual.
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
        catch
        {
            // Резерв читается только как подсказка; ошибка чтения не ломает включение.
        }

        return null;
    }
}
