using System.IO;
using System.Reflection;
using Microsoft.Win32;
using SCU.Common;

namespace SCU.Infrastructure.Windows;

// Автозапуск SCU: значение "SCU" в HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
// Записью управляют два места — тумблер «Автозапуск» в «Настройках» и согласие
// из переводчика раскладки (раздел «Ввод»); логика собрана здесь, чтобы оба
// писали одну и ту же команду и не спорили между собой.
internal static class ScuAutorun
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "SCU";

    // Аргумент старта в трее: автозапуск происходит после загрузки системы —
    // окно не должно выезжать на экран, приложение садится в область уведомлений.
    public const string TrayArg = "--tray";

    // Метка владения: значение, записанное нами последним. Без неё чужие записи
    // (пользовательские или от старых версий) не трогаем.
    public static string GetOwnershipPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU",
        "layout-translator-run-owner.txt");

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

    public static string? ReadOwnedValue()
    {
        try
        {
            var path = GetOwnershipPath();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    // Автозапуск включён: запись "SCU" в Run существует и непуста. Источник
    // истины для тумблера «Настроек» и для проверок переводчика.
    public static bool IsEnabled()
    {
        var value = ReadCurrentValue();
        return !string.IsNullOrWhiteSpace(value);
    }

    // Включение: команда Run + метка владения. Метка пишется до реестра — при
    // сбое SetValue она не превратит ещё не созданную запись в «чужую».
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

            var ownershipPath = GetOwnershipPath();
            var directory = Path.GetDirectoryName(ownershipPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(ownershipPath, command);
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warn("AUTORUN | включение не выполнено: " + ex.GetType().Name);
            return false;
        }
    }

    // Выключение: запись и метка удаляются всегда. Вызывается только по явному
    // действию пользователя (тумблер «Настроек» или уже проверенное владение
    // у переводчика) — чужие записи тут не защищаются.
    public static bool TryDelete(Logger? logger = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);

            try { File.Delete(GetOwnershipPath()); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warn("AUTORUN | выключение не выполнено: " + ex.GetType().Name);
            return false;
        }
    }

    // Команда Run: путь к exe + аргумент старта в трее. В режиме разработки
    // приложение запущено через dotnet.exe — добавляем путь к entry assembly,
    // иначе Run запустил бы только CLI.
    public static string? BuildCommand()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        var command = "\"" + exe + "\" " + TrayArg;
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(entryAssembly) && File.Exists(entryAssembly))
            {
                command += " \"" + entryAssembly + "\"";
            }
        }

        return command;
    }
}
