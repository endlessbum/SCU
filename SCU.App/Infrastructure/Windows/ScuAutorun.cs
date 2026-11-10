using System.IO;
using System.Reflection;
using Microsoft.Win32;
using SCU.Common;

namespace SCU.Infrastructure.Windows;

// Автозапуск SCU: значение "SCU" в HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
// Записью управляет тумблер «Автозапуск» в «Настройках».
internal static class ScuAutorun
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "SCU";

    // Аргумент старта в трее: автозапуск происходит после загрузки системы —
    // окно не должно выезжать на экран, приложение садится в область уведомлений.
    public const string TrayArg = "--tray";

    public static string? ReadCurrentValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    // Автозапуск включён: запись "SCU" в Run существует и непуста. Источник
    // истины для тумблера «Настроек».
    public static bool IsEnabled()
    {
        var value = ReadCurrentValue();
        return !string.IsNullOrWhiteSpace(value);
    }

    // Включение: команда Run с аргументом старта в трее.
    public static bool TryWrite(Logger? logger = null)
    {
        try
        {
            var command = BuildCommand();
            if (command is null)
            {
                logger?.Warn("AUTORUN | не удалось определить путь запуска SCU");
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                logger?.Warn("AUTORUN | не удалось открыть раздел автозапуска SCU");
                return false;
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warn("AUTORUN | включение не выполнено: " + ex.GetType().Name);
            return false;
        }
    }

    // Выключение: запись Run\SCU удаляется по явному действию пользователя.
    public static bool TryDelete(Logger? logger = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warn("AUTORUN | выключение не выполнено: " + ex.GetType().Name);
            return false;
        }
    }

    // Команда Run: путь к exe + аргумент старта в трее. В режиме разработки
    // приложение запущено через dotnet.exe — добавляем путь к entry assembly
    // ПЕРЕД аргументом: dotnet CLI трактует всё после пути приложения как его
    // аргументы, а «--tray» до пути считает своей опцией и падает (автозапуск
    // молча не срабатывал, тумблер при этом показывал «включён»).
    // exePath — точка для тестов; null берёт текущий процесс.
    public static string? BuildCommand(string? exePath = null)
    {
        var exe = exePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        var command = "\"" + exe + "\"";
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly) || !File.Exists(entryAssembly))
            {
                // Команду не строим: TryWrite вернёт false и тумблер не
                // покажет «включён» для заведомо нерабочей записи.
                return null;
            }

            command += " \"" + entryAssembly + "\"";
        }

        return command + " " + TrayArg;
    }
}
