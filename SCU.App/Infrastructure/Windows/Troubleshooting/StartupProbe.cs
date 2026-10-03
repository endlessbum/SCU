using Microsoft.Win32;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Автозагрузка: ключи Run/RunOnce (HKCU и HKLM) + папки автозагрузки.
/// Ищем недействительные записи (исполняемый файл не существует) и дубликаты.
/// Большое количество записей само по себе не считается неполадкой.
/// </summary>
public sealed class StartupProbe : DiagnosticProbe
{
    public override string Id => "startup";

    public override string Title => "Проверка автозагрузки";

    public override Task CollectAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var entries = new List<(string Name, string Command, string Source)>();

        entries.AddRange(ReadRunKey(
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU Run"));
        entries.AddRange(ReadRunKey(
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU RunOnce"));
        entries.AddRange(ReadRunKey(
            Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM Run"));
        entries.AddRange(ReadRunKey(
            Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM RunOnce"));
        // 32-битные Run-ключи. На 32-битной ОС ключа WOW6432Node нет — ReadRunKey вернёт пусто.
        // Упакованные StartupTask сюда не добавляем: у них нет пути к exe,
        // а проверка этого зонда — «исполняемый файл не существует».
        entries.AddRange(ReadRunKey(
            Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM Run (32)"));
        entries.AddRange(ReadRunKey(
            Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM RunOnce (32)"));
        entries.AddRange(ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Папка автозагрузки (пользователь)"));
        entries.AddRange(ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Папка автозагрузки (общая)"));

        var snapshots = new List<StartupEntrySnapshot>(entries.Count);
        foreach (var (name, command, source) in entries)
        {
            var problem = DescribeProblem(command);
            if (problem is not null)
            {
                snapshots.Add(new StartupEntrySnapshot(name, command, source, problem));
            }
        }

        // Дубликаты: одна и та же команда назначена дважды из разных мест.
        var duplicates = entries
            .GroupBy(entry => entry.Command.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        context.StartupEntries = snapshots
            .Concat(duplicates.Select(command => new StartupEntrySnapshot(
                string.Empty, command, string.Empty, "duplicate")))
            .ToList();
    }, CancellationToken.None);

    private static List<(string Name, string Command, string Source)> ReadRunKey(
        RegistryKey root, string subKey, string source)
    {
        var entries = new List<(string Name, string Command, string Source)>();
        try
        {
            using var key = root.OpenSubKey(subKey);
            if (key is null)
            {
                return entries;
            }

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string command && !string.IsNullOrWhiteSpace(command))
                {
                    entries.Add((name, command, source));
                }
            }
        }
        catch
        {
            // Нет доступа к ключу — записи этого источника просто не попадают в сбор.
        }

        return entries;
    }

    private static List<(string Name, string Command, string Source)> ReadStartupFolder(
        string folder, string source)
    {
        var entries = new List<(string Name, string Command, string Source)>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return entries;
        }

        try
        {
            entries.AddRange(Directory
                .EnumerateFiles(folder)
                .Select(file => (Path.GetFileName(file), file, source)));
        }
        catch
        {
            // Нечитаемая папка — пропускаем.
        }

        return entries;
    }

    // null — с записью всё в порядке (или она нечитаема: не строим правила на догадках).
    private static string? DescribeProblem(string command)
    {
        var path = ExtractExecutablePath(command);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return "missing";
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    // Достаём путь исполняемого файла из команды запуска (кавычки, аргументы, env-переменные).
    private static string? ExtractExecutablePath(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing] : null;
        }

        // Без кавычек: берём токен до первого пробела, но допускаем «C:\Program Files\...»
        // только в кавычках — голый путь с пробелами надёжно не разбирается.
        var space = trimmed.IndexOf(' ');
        var token = space > 0 ? trimmed[..space] : trimmed;
        return token.Contains('\\', StringComparison.Ordinal) ? token : null;
    }
}
