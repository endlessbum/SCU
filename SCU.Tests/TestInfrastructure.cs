using System.Runtime.CompilerServices;
using Xunit;

// Интеграционные тесты ScannerCore работают с общими ресурсами (реестровые
// test-hook'и, дисковый кэш сканера, база IOC): параллельный запуск ломает
// их взаимно. П. 10 аудита: тесты не должны проходить молча друг сквозь друга.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Единая точка создания временных файлов/каталогов для тестов. Обычное место —
// %TEMP%\SCU.Tests; если окружение запрещает запись туда (песочницы агентов,
// защитные политики), автоматически используется каталог рядом со сборкой
// тестов. Все тесты должны создавать временные данные только через TestScratch.
public static class TestScratch
{
    private static readonly string _root = ResolveRoot();

    public static string Root => _root;

    private static string ResolveRoot()
    {
        var candidate = Path.Combine(Path.GetTempPath(), "SCU.Tests");
        if (IsWritable(candidate))
            return candidate;

        var local = Path.Combine(AppContext.BaseDirectory, "test-tmp");
        if (IsWritable(local))
            return local;

        throw new InvalidOperationException(
            "Нет доступного каталога для временных файлов тестов: " + candidate + " и " + local);
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Создаёт и возвращает уникальный каталог вида <c>[prefix-]guid</c> под корнем.</summary>
    public static string NewDir(string? prefix = null)
    {
        var name = string.IsNullOrEmpty(prefix)
            ? Guid.NewGuid().ToString("N")
            : $"{prefix}-{Guid.NewGuid():N}";
        return Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
    }

    /// <summary>Возвращает путь к уникальному файлу вида <c>[prefix_]guid.ext</c> (без создания файла).</summary>
    public static string NewFile(string prefix, string extension)
    {
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        return Path.Combine(_root, $"{prefix}_{Guid.NewGuid():N}{extension}");
    }
}

// Возможности окружения, которые определяются один раз и используются тестами
// для честного динамического пропуска (SkippableFact): в изолированных песочницах
// запись в HKCU может быть запрещена вовсе — тогда реестровые тесты пропускаются
// с явной причиной вместо безликой UnauthorizedAccessException.
public static class TestEnvironment
{
    private static readonly bool _registryWritable = DetectRegistryWritable();

    /// <summary>Доступна ли запись в HKCU из хоста тестов.</summary>
    public static bool RegistryWritable => _registryWritable;

    private static bool DetectRegistryWritable()
    {
        const string probeKey = @"Software\SCUTests\RegApply\.write-probe";
        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(probeKey, writable: true);
            key!.SetValue("probe", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.DeleteValue("probe");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}

// Раннее исправление окружения для дочерних процессов (ScannerCore):
// 1) дисковый кэш сканера строится из %ProgramData% (env-переменной);
// 2) ArchiveScanner распаковывает члены через GetTempPathW, т.е. %TEMP%.
// В изолированных песочницах запись в настоящий ProgramData/Temp может быть
// запрещена — тогда обе переменные окружения переводятся в каталог рядом с
// тестовым workspace, и тесты работают зеркально, трогая только свои данные.
internal static class TestHostSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var programData = Environment.GetEnvironmentVariable("ProgramData");
            if (IsWritableDirectory(Path.Combine(programData ?? string.Empty, "SCU", "scanner-cache")))
            {
                return; // обычное окружение: стандартные пути остаются.
            }

            var workspace = Directory.CreateDirectory(Path.Combine(TestScratch.Root, "environment"));
            var program = Directory.CreateDirectory(Path.Combine(workspace.FullName, "programdata")).FullName;
            var temp = Directory.CreateDirectory(Path.Combine(workspace.FullName, "temp")).FullName;

            // ScannerCore читает ProgramData и Temp динамически из окружения;
            // помощники тестов читают те же переменные — путь один на обеих сторонах.
            Environment.SetEnvironmentVariable("ProgramData", program);
            Environment.SetEnvironmentVariable("TEMP", temp);
            Environment.SetEnvironmentVariable("TMP", temp);
        }
        catch (Exception)
        {
            // Без «хитростей окружения» тесты развернутся честным Assert.Fail.
        }
    }

    private static bool IsWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
