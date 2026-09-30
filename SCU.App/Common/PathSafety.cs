using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SCU.Common;

// Безопасная проверка границы пути. Заменяет проверки вида
// child.StartsWith(parent): префиксное сравнение пропускает соседние каталоги
// (C:\Users\Alex2 начинается с C:\Users\Alex, но не является дочерним).
// Существующий префикс канонизируется через GetFinalPathNameByHandle, чтобы
// junction/symlink, указывающие наружу parent, не проходили как «внутри».
public static class PathSafety
{
    // true, если child строго внутри parent (не равен ему).
    public static bool IsUnderDirectory(string? child, string? parent) =>
        IsUnder(child, parent, allowEqual: false);

    // true, если child внутри parent или совпадает с ним (файл в корне папки).
    public static bool IsUnderDirectoryOrEqual(string? child, string? parent) =>
        IsUnder(child, parent, allowEqual: true);

    // Канонический путь существующего файла/каталога: junction/symlink
    // раскрываются в реальное расположение. false — путь не существует,
    // некорректен или недоступен (fail-closed: считаем, что перенаправление
    // обнаружить не удалось).
    public static bool TryResolveFinalPath(string? path, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                return false;
            }

            resolved = ResolveExistingPrefix(full);
            return resolved.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or System.Security.SecurityException
            or UnauthorizedAccessException)
        {
            resolved = string.Empty;
            return false;
        }
    }

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
        catch (Exception exception) when (exception is ArgumentException or IOException or System.Security.SecurityException
            or UnauthorizedAccessException)
        {
            // Некорректный/недоступный/нерезолвируемый путь — не «внутри» (fail-closed).
            return false;
        }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return ResolveExistingPrefix(full);
    }

    // Самый длинный существующий префикс резолвится в конечный путь (reparse),
    // хвост несуществующих компонентов дописывается к цели. Так junction
    // C:\safe\link → C:\outside даёт C:\outside\file, а не C:\safe\link\file.
    private static string ResolveExistingPrefix(string full)
    {
        var missing = new List<string>();
        var current = full;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                // Fail-closed: не открывается/не резолвится (ACL, исчезновение между
                // Exists и открытием) — нельзя доказать, что путь не уводит за родителя.
                // Раньше здесь был fallback на нераскрученный путь, из-за чего junction
                // при неудаче резолва проходил проверку как «внутри». IOException ловят
                // и IsUnder (→ «не внутри»), и TryResolveFinalPath (→ false).
                var resolved = TryGetFinalPath(current)
                    ?? throw new IOException($"GetFinalPathNameByHandle failed for {current}");
                resolved = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (missing.Count == 0)
                {
                    return resolved;
                }

                missing.Reverse();
                return Path.GetFullPath(Path.Combine([resolved, .. missing]))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            var name = Path.GetFileName(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(name)
                || string.IsNullOrEmpty(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            missing.Add(name);
            current = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return full;
    }

    private static string? TryGetFinalPath(string path)
    {
        using var handle = CreateFile(
            path,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new StringBuilder(520);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            return null;
        }

        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                return null;
            }
        }

        return StripExtendedPrefix(buffer.ToString());
    }

    private static string StripExtendedPrefix(string path)
    {
        const string unc = @"\\?\UNC\";
        const string dos = @"\\?\";
        if (path.StartsWith(unc, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[unc.Length..];
        }

        if (path.StartsWith(dos, StringComparison.OrdinalIgnoreCase))
        {
            return path[dos.Length..];
        }

        return path;
    }

    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
