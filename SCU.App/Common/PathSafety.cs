namespace SCU.Common;

// Безопасная проверка границы пути. Заменяет проверки вида
// child.StartsWith(parent): префиксное сравнение пропускает соседние каталоги
// (C:\Users\Alex2 начинается с C:\Users\Alex, но не является дочерним).
public static class PathSafety
{
    // true, если child строго внутри parent (не равен ему).
    public static bool IsUnderDirectory(string? child, string? parent) =>
        IsUnder(child, parent, allowEqual: false);

    // true, если child внутри parent или совпадает с ним (файл в корне папки).
    public static bool IsUnderDirectoryOrEqual(string? child, string? parent) =>
        IsUnder(child, parent, allowEqual: true);

    private static bool IsUnder(string? child, string? parent, bool allowEqual)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        try
        {
            var fullChild = Normalize(child);
            var fullParent = Normalize(parent);
            if (fullChild.Length == 0 || fullParent.Length == 0)
            {
                return false;
            }

            if (string.Equals(fullChild, fullParent, StringComparison.OrdinalIgnoreCase))
            {
                return allowEqual;
            }

            // GetRelativePath на Windows сравнивает компоненты пути без учёта
            // регистра; relative вида "..", "..\..." или корневой (другой диск)
            // означает «вне parent».
            var relative = Path.GetRelativePath(fullParent, fullChild);
            return relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or System.Security.SecurityException)
        {
            // Некорректный/недоступный путь — не «внутри» (fail-closed).
            return false;
        }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
