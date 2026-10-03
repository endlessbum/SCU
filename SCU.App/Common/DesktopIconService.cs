using System.IO;
using System.Runtime.InteropServices;

namespace SCU.Common;

// Иконка ярлыков приложения в системе (рабочий стол, меню «Пуск»). Встроенную
// иконку SCU.exe в рантайме менять нельзя, поэтому с приложением ставится набор
// окрашенных .ico (Assets\Icons\SCU-<пресет>-light.ico, генерируются
// tools/AccentIconGenerator), а сервис перепривязывает IconLocation ярлыков на
// файл выбранного цвета. Ярлык инсталлятора указывает на иконку exe — при первом
// запуске он переезжает на цвет из settings.json, дальше следует за настройкой.
public static class DesktopIconService
{
    private const int ShcneUpdateItem = 0x00002000;
    private const uint ShcnfPathW = 0x0005;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    // Имя файла иконки: пресет ThemeManager. Вариант один для обеих тем —
    // «light» (5 файлов SCU-<пресет>-light.ico); тёмные варианты убраны.
    internal static string GetIconFileName(AppAccent accent) =>
        $"SCU-{accent.ToString().ToLowerInvariant()}-light.ico";

    // Перепривязка ярлыков на иконку выбранного цвета. Сбои некритичны (нет прав
    // на общий ярлык, COM недоступен, набора файлов нет в dev-запуске) — в журнал,
    // приложение продолжает работать со встроенной иконкой exe.
    public static void Apply(AppAccent accent, Logger logger)
    {
        try
        {
            var iconPath = Path.Combine(
                AppContext.BaseDirectory, "Assets", "Icons", GetIconFileName(accent));
            if (!File.Exists(iconPath))
            {
                // Папка без набора иконок: ярлыки остаются на иконке exe.
                logger.Warn("ICON | accent icon missing | " + iconPath);
                return;
            }

            var updated = 0;
            foreach (var shortcutPath in GetShortcutPaths())
            {
                try
                {
                    if (File.Exists(shortcutPath) && TrySetShortcutIcon(shortcutPath, iconPath))
                    {
                        updated++;
                    }
                }
                catch (Exception exception)
                {
                    logger.Warn("ICON | shortcut update failed | " + shortcutPath + " | " + exception.Message);
                }
            }

            if (updated > 0)
            {
                logger.Info($"ICON | shortcuts retargeted to {Path.GetFileName(iconPath)} ({updated})");
            }
        }
        catch (Exception exception)
        {
            logger.Warn("ICON | apply failed | " + exception.Message);
        }
    }

    // Ярлыки инсталлятора: {autodesktop}\SCU (общий рабочий стол при per-machine
    // установке) и {group}\SCU — Programs\SCU (общее меню). Пользовательские
    // расположения проверяются тоже: установка могла быть перенесена.
    private static IEnumerable<string> GetShortcutPaths()
    {
        foreach (var desktop in new[]
                 {
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.CommonDesktopDirectory
                 })
        {
            yield return Path.Combine(Environment.GetFolderPath(desktop), "SCU.lnk");
        }

        foreach (var menu in new[]
                 {
                     Environment.SpecialFolder.Programs,
                     Environment.SpecialFolder.CommonPrograms
                 })
        {
            yield return Path.Combine(Environment.GetFolderPath(menu), "SCU", "SCU.lnk");
        }
    }

    // true — ярлык изменён и оболочка уведомлена; false — уже указывает на нужный
    // файл (повторный Apply на каждом запуске не должен дёргать ярлыки и кэш иконок).
    private static bool TrySetShortcutIcon(string shortcutPath, string iconPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return false;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            var current = ((string)shortcut.IconLocation).Split(',')[0].Trim();
            if (string.Equals(current, iconPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            shortcut.IconLocation = iconPath + ",0";
            shortcut.Save();
            NotifyShell(shortcutPath);
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    // Иначе Explorer показывает старую иконку из кэша до перезапуска оболочки.
    private static void NotifyShell(string path)
    {
        var pointer = Marshal.StringToHGlobalUni(path);
        try
        {
            SHChangeNotify(ShcneUpdateItem, ShcnfPathW, pointer, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
