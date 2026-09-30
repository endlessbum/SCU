using SCU.Common;

namespace SCU.Infrastructure.Browser;

// Безопасная обработка имени файла загрузки. Имя из Content-Disposition / URL
// никогда не доверяется: нормализация, защита от path traversal, абсолютных
// путей, UNC, зарезервированных имён Windows и дубликатов в папке загрузок.
public static class BrowserDownloadPolicy
{
    // Широкий список: ShellExecute выполняет не только PE-файлы (.lnk ведёт на цель,
    // .reg импортирует реестр, .application ставит ClickOnce и т.д.). Для всего
    // остального пользовательский диалог открытия — в OpenDownloadFile.
    private static readonly string[] ExecutableExtensions =
    [
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".scr", ".com", ".vbs", ".js", ".wsf", ".hta", ".dll",
        ".lnk", ".url", ".application", ".appref-ms", ".msix", ".appx", ".msp", ".mst",
        ".pif", ".cpl", ".msc", ".chm", ".jar", ".reg", ".iso", ".inf", ".py", ".pyw",
    ];

    // Граница папки загрузок; null — безопасное имя построить не удалось.
    public static string? BuildDownloadPath(string downloadFolder, string? suggestedFileName)
    {
        var safeName = SanitizeFileName(suggestedFileName);
        if (safeName is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(downloadFolder);
            var path = Path.Combine(downloadFolder, safeName);

            // Финальная проверка границы: результат обязан остаться внутри папки.
            if (!PathSafety.IsUnderDirectory(path, downloadFolder))
            {
                return null;
            }

            // null = уникальное имя построить не удалось — загрузка отменяется,
            // а не перезаписывает существующий файл.
            return EnsureUnique(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static string? SanitizeFileName(string? suggestedFileName)
    {
        var raw = (suggestedFileName ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        // Абсолютный путь / UNC / корень — берём только имя файла.
        try
        {
            raw = Path.GetFileName(raw);
        }
        catch (Exception exception) when (exception is ArgumentException)
        {
            return null;
        }

        if (raw.Length == 0)
        {
            return null;
        }

        // Имя может прийти с URL-кодированием (%2E%2E%5C) — расшифровываем
        // и повторно отрезаем путь.
        var decoded = Uri.UnescapeDataString(raw);
        if (decoded != raw)
        {
            try
            {
                decoded = Path.GetFileName(decoded);
            }
            catch (ArgumentException)
            {
                return null;
            }
            raw = decoded.Length > 0 ? decoded : raw;
        }

        // Каталоги и traversal внутри «имени».
        raw = raw.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

        // Управляющие символы и запрещённые в Windows знаки.
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var name = builder.ToString().Trim(' ', '.');
        if (name.Length == 0)
        {
            return null;
        }

        // Базовое имя без расширения не должно совпадать с зарезервированным.
        var baseName = name.Split('.')[0].ToUpperInvariant();
        if (baseName is "CON" or "PRN" or "AUX" or "NUL"
            || baseName.StartsWith("COM", StringComparison.Ordinal) && baseName.Length is 4 && char.IsAsciiDigit(baseName[3])
            || baseName.StartsWith("LPT", StringComparison.Ordinal) && baseName.Length is 4 && char.IsAsciiDigit(baseName[3]))
        {
            name = "_" + name;
        }

        const int maxFileName = 180;
        if (name.Length > maxFileName)
        {
            var extension = Path.GetExtension(name);
            name = string.IsNullOrEmpty(extension)
                ? name[..maxFileName]
                : name[..(maxFileName - extension.Length)] + extension;
        }

        return name;
    }

    public static bool IsExecutable(string path)
    {
        var extension = Path.GetExtension(path ?? string.Empty);
        return ExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string? EnsureUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Все варианты заняты: перезаписывать существующий файл нельзя.
        return null;
    }
}
