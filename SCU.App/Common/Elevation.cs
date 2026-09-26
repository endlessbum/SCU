using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace SCU.Common;

public static class Elevation
{
    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string? ResolveExecutablePath()
    {
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "SCU.exe");
        if (File.Exists(fromOutput))
        {
            return fromOutput;
        }

        var fromProcess = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(fromProcess))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(fromProcess);
        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(fromOutput) ? fromOutput : null;
        }

        return fromProcess;
    }

    public static Result RelaunchElevated()
    {
        if (IsAdmin())
        {
            return Result.Success("Уже запущено с правами администратора.");
        }

        try
        {
            var executable = ResolveExecutablePath();
            if (string.IsNullOrWhiteSpace(executable))
            {
                return Result.Failure("Не удалось определить путь SCU.exe. Соберите проект и запускайте exe, а не хост dotnet.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return Result.Failure("Система не запустила повышенный процесс.");
            }

            return Result.Success("Запрошен перезапуск с правами администратора.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return Result.Failure("Повышение прав отменено пользователем.", 1223);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось перезапустить приложение: " + exception.Message);
        }
    }
}
