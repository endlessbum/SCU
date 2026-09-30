namespace SCU.Common;

// Абсолютные пути системных утилит. Запуск по голому имени ("netsh", "powercfg")
// включает в поиск CreateProcess каталог приложения и текущий каталог: положенный
// туда одноимённый exe выполнился бы с правами SCU. Абсолютный путь в System32
// поиск не задействует вовсе.
public static class SystemTool
{
    // Windows PowerShell лежит не в корне System32, а в собственном подкаталоге:
    // на твикнутых системах System32\powershell.exe бывает удалён, канонический
    // путь остаётся всегда (см. также SCURunner.ResolvePowerShellPath).
    private static readonly string CanonicalPowerShell = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string Path(string executable)
    {
        var name = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executable
            : executable + ".exe";
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.Equals(name, "powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback на старое расположение — на случай нестандартной системы.
            return File.Exists(CanonicalPowerShell)
                ? CanonicalPowerShell
                : System.IO.Path.Combine(systemDirectory, name);
        }

        return System.IO.Path.Combine(systemDirectory, name);
    }
}
