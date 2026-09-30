using System.Diagnostics;
using System.Text.Json;
using SCU.Common;

namespace SCU.Infrastructure.Storage;

// Данные одного пользовательского скрипта (карточка в разделе меню).
public sealed class UserScriptData
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;

    // ".ps1" или ".bat" — определяется по выбранному файлу.
    public string Extension { get; set; } = ".ps1";

    // Раздел меню, в который пользователь поместил карточку.
    public int SectionNumber { get; set; }

    // SHA-256 копии на диске, зафиксированный при импорте. Пустой — legacy-запись
    // до появления проверки: эталон фиксируется при первом запуске (TOFU).
    public string Sha256 { get; set; } = string.Empty;
}

// Результат проверки работоспособности скрипта.
public sealed record ScriptValidationResult(bool Ok, string Message);

// Хранилище пользовательских скриптов: %AppData%\SCU\scripts — копии файлов
// (самодостаточно, оригинал можно удалить) + scripts.json с карточками.
// Проверка работоспособности БЕЗ запуска: .ps1 — синтаксический разбор Parser'ом
// PowerShell (выполнение скрипта при проверке недопустимо — побочные эффекты),
// .bat — читаемость/непустота (безопасного синтаксического анализатора нет).
public sealed class UserScriptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Logger _logger;
    private readonly string _directory;
    private readonly string _jsonPath;

    public UserScriptStore(Logger logger, string? directory = null)
    {
        _logger = logger;
        _directory = directory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCU", "scripts");
        _jsonPath = Path.Combine(_directory, "scripts.json");
    }

    public string ScriptsDirectory => _directory;

    public string ScriptPath(UserScriptData data) => Path.Combine(_directory, data.Id + data.Extension);

    public List<UserScriptData> Load()
    {
        try
        {
            if (!File.Exists(_jsonPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<UserScriptData>>(File.ReadAllText(_jsonPath)) ?? [];
        }
        catch (Exception exception)
        {
            _logger.Warn("USCRIPT | load failed | " + exception.Message);
            return [];
        }
    }

    public void Save(IReadOnlyList<UserScriptData> scripts)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var temp = _jsonPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(scripts, JsonOptions));
            File.Move(temp, _jsonPath, overwrite: true);
        }
        catch (Exception exception)
        {
            _logger.Warn("USCRIPT | save failed | " + exception.Message);
        }
    }

    // Импорт выбранного файла в хранилище: копия с устойчивым именем <id><ext>.
    public UserScriptData Import(string sourcePath, int sectionNumber, string title,
        string comment, string tooltip, string id)
    {
        Directory.CreateDirectory(_directory);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var data = new UserScriptData
        {
            Id = id,
            Title = title,
            Comment = comment,
            Tooltip = tooltip,
            Extension = extension,
            SectionNumber = sectionNumber,
        };
        File.Copy(sourcePath, ScriptPath(data), overwrite: true);
        data.Sha256 = ComputeSha256(ScriptPath(data)) ?? string.Empty;
        return data;
    }

    public void Delete(UserScriptData data)
    {
        try
        {
            if (File.Exists(ScriptPath(data)))
            {
                File.Delete(ScriptPath(data));
            }
        }
        catch (Exception exception)
        {
            _logger.Warn("USCRIPT | delete file failed | " + exception.Message);
        }
    }

    // Проверка расширения: поддерживаются только .ps1 и .bat.
    public static bool IsSupportedExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ps1" or ".bat";

    // Проверка работоспособности БЕЗ выполнения.
    public async Task<ScriptValidationResult> ValidateAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ScriptValidationResult(false, L.T("Файл не найден."));
            }

            if (!IsSupportedExtension(path))
            {
                return new ScriptValidationResult(false, L.T("Поддерживаются только .ps1 и .bat."));
            }

            if (new FileInfo(path).Length == 0)
            {
                return new ScriptValidationResult(false, L.T("Скрипт пустой."));
            }

            if (Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                return await ValidatePowerShellAsync(path, ct).ConfigureAwait(false);
            }

            return ValidateBatch(path);
        }
        catch (Exception exception)
        {
            return new ScriptValidationResult(false, exception.Message);
        }
    }

    // Синтаксический разбор PowerShell: ошибки парсера — скрипт битый; сам код
    // НЕ выполняется (только ParseFile), побочных эффектов нет.
    private async Task<ScriptValidationResult> ValidatePowerShellAsync(string path, CancellationToken ct)
    {
        var command =
            "$e=$null; $t=$null; " +
            "[void][System.Management.Automation.Language.Parser]::ParseFile(" +
            $"'{path.Replace("'", "''")}', [ref]$t, [ref]$e); " +
            "if($e -and $e.Count -gt 0){ $e | Select-Object -First 3 | ForEach-Object { $_.Message } } " +
            "else { 'SCU_SCRIPT_OK' }";

        var output = await RunCaptureAsync(SystemTool.Path("powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", command], ct).ConfigureAwait(false);

        if (output.ExitCode != 0)
        {
            return new ScriptValidationResult(false, L.T("Не удалось запустить проверку (PowerShell)."));
        }

        return output.Output.Contains("SCU_SCRIPT_OK", StringComparison.Ordinal)
            ? new ScriptValidationResult(true, string.Empty)
            : new ScriptValidationResult(false, output.Output.Trim());
    }

    private static ScriptValidationResult ValidateBatch(string path)
    {
        try
        {
            // Безопасная проверка .bat без выполнения: читаемость и отсутствие
            // бинарного мусора (нули) — синтаксического анализатора batch нет.
            using var stream = File.OpenRead(path);
            var buffer = new byte[4096];
            int read;
            long total = 0;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == 0)
                    {
                        return new ScriptValidationResult(false, L.T("Файл повреждён (бинарные данные)."));
                    }
                }

                total += read;
            }

            return total > 0
                ? new ScriptValidationResult(true, string.Empty)
                : new ScriptValidationResult(false, L.T("Скрипт пустой."));
        }
        catch (Exception exception)
        {
            return new ScriptValidationResult(false, L.T("Файл не читается: {0}", exception.Message));
        }
    }

    // Запуск сохранённого скрипта: возвращает код выхода и хвост вывода.
    // Перед запуском — проверка целостности копии: файл в %AppData% может
    // изменить другой процесс, а выполняется скрипт с правами SCU
    // (администратор). Расхождение хэша — запуск отменяется (fail-closed).
    public async Task<(int ExitCode, string Output)> RunAsync(UserScriptData data, CancellationToken ct = default)
    {
        var path = ScriptPath(data);

        var actualSha = ComputeSha256(path);
        if (actualSha is null)
        {
            _logger.Warn("USCRIPT | integrity | unreadable | " + data.Id);
            return (-1, L.T("Файл скрипта не найден или не читается — запуск отменён."));
        }

        if (string.IsNullOrEmpty(data.Sha256))
        {
            // Legacy-карточка без эталона: текущее состояние на диске становится
            // эталоном (TOFU). Сохранение списка делает вызывающая сторона.
            data.Sha256 = actualSha;
            _logger.Warn("USCRIPT | integrity | hash fixed on first run | " + data.Id);
        }
        else if (!string.Equals(actualSha, data.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Error("USCRIPT | integrity | modified after import | " + data.Id);
            return (-1, L.T("Файл скрипта изменён после добавления — запуск отменён."));
        }

        var (fileName, arguments) = data.Extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            ? (SystemTool.Path("powershell.exe"), new List<string>
               {
                   "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path,
               })
            : (SystemTool.Path("cmd.exe"), new List<string> { "/d", "/c", path });

        var result = await RunCaptureAsync(fileName, arguments, ct).ConfigureAwait(false);
        var tail = result.Output.Length > 600
            ? "…" + result.Output[^600..]
            : result.Output;
        return (result.ExitCode, tail.Trim());
    }

    // SHA-256 файла; null — файл не читается (отсутствует или занят).
    private static string? ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCaptureAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        return (process.ExitCode, output);
    }
}
