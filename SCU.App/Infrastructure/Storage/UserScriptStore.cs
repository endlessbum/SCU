using System.Diagnostics;
using System.Text.Json;
using SCU.Common;
using SCU.Interop;

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

    // false — сохранить не удалось: вызывающая сторона обязана не сообщать
    // пользователю успех (иначе scripts.json расходится с реальностью).
    public bool Save(IReadOnlyList<UserScriptData> scripts)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var temp = _jsonPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(scripts, JsonOptions));
            File.Move(temp, _jsonPath, overwrite: true);
            return true;
        }
        catch (Exception exception)
        {
            // П. №15 аудита: остаток .tmp после сбоя — мусор и ложный
            // «незавершённый файл» при следующем чтении.
            try { File.Delete(_jsonPath + ".tmp"); } catch { /* .tmp мог не создаться */ }
            _logger.Warn("USCRIPT | save failed | " + exception.Message);
            return false;
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

    // Сохранение отредактированного в редакторе содержимого: запись файла +
    // пересчёт эталонного SHA-256 (иначе следующий запуск отменяется по
    // целостности) + persistence scripts.json. Экземпляр data заменяет запись
    // с тем же Id в scripts.json (у вызывающих — свой экземпляр из Load()).
    // false — эталон/список не обновились, правку считать не сохранённой.
    public bool UpdateContent(UserScriptData data, string text)
    {
        // Полная транзакционность (аудит HIGH): порядок temp → replace target →
        // persist metadata, с откатом целевого файла и SHA при любом сбое.
        // Раньше data.Sha256 мутировался до Move: сбой Move оставлял scripts.json
        // с новым SHA против старого файла — скрипт навсегда блокировался
        // проверкой целостности, а in-memory объект был изменён при return false.
        var target = ScriptPath(data);
        var temp = target + ".new";
        var backup = target + ".old";
        var oldSha = data.Sha256;
        var targetExisted = File.Exists(target);

        try
        {
            File.WriteAllText(temp, text);
        }
        catch (Exception exception)
        {
            _logger.Warn("USCRIPT | update content failed | " + data.Id + " | " + exception.Message);
            return false;
        }

        var sha = ComputeSha256(temp);
        if (sha is null)
        {
            // Файл записан, но не читается — пустой хэш оставлять нельзя:
            // он включил бы TOFU на чужом содержимом при первом запуске.
            _logger.Error("USCRIPT | update content | hash unreadable | " + data.Id);
            try { File.Delete(temp); } catch { /* уже удалён */ }
            return false;
        }

        if (targetExisted)
        {
            // Откаточная копия старого контента — до подмены целевого файла.
            try
            {
                File.Copy(target, backup, overwrite: true);
            }
            catch (Exception exception)
            {
                _logger.Warn("USCRIPT | update content | old backup failed | " + data.Id + " | " + exception.Message);
                try { File.Delete(temp); } catch { /* уже удалён */ }
                return false;
            }
        }

        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception exception)
        {
            // Целевой файл не подменён — metadata и in-memory не тронуты.
            _logger.Error("USCRIPT | update content | replace failed | " + data.Id + " | " + exception.Message);
            try { File.Delete(temp); } catch { /* уже удалён */ }
            try { File.Delete(backup); } catch { /* копии могло не быть */ }
            return false;
        }

        data.Sha256 = sha;
        var scripts = Load();
        var index = scripts.FindIndex(script => script.Id == data.Id);
        if (index >= 0)
        {
            scripts[index] = data;
        }
        else
        {
            scripts.Add(data);
        }

        if (Save(scripts))
        {
            try { File.Delete(backup); } catch { /* копия не создавалась */ }
            return true;
        }

        // Metadata не сохранилась — откат целевого файла и in-memory SHA
        // к состоянию до правки (аудит: metadata и файл обязаны совпадать).
        try
        {
            if (targetExisted)
            {
                File.Move(backup, target, overwrite: true);
            }
            else
            {
                File.Delete(target);
            }

            data.Sha256 = oldSha;
            var rolledBack = Load();
            var rollbackIndex = rolledBack.FindIndex(script => script.Id == data.Id);
            if (rollbackIndex >= 0)
            {
                rolledBack[rollbackIndex] = data;
                Save(rolledBack);
            }
        }
        catch (Exception rollbackException)
        {
            _logger.Error("USCRIPT | update content | rollback failed | " + data.Id + " | " + rollbackException.Message);
        }
        finally
        {
            try { File.Delete(backup); } catch { /* уже возвращён на место */ }
        }

        return false;
    }

    // true — файла нет или удалён; false — занят/не удалился: запись из
    // scripts.json в этом случае убирать нельзя, иначе файл останется сиротой.
    public bool Delete(UserScriptData data)
    {
        try
        {
            if (File.Exists(ScriptPath(data)))
            {
                File.Delete(ScriptPath(data));
            }

            return true;
        }
        catch (Exception exception)
        {
            _logger.Warn("USCRIPT | delete file failed | " + exception.Message);
            return false;
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

        // Таймаут (аудит): TimeoutException из RunCaptureAsync превращается в
        // честный статус карточки, а не в необработанное исключение.
        try
        {
            var result = await RunCaptureAsync(fileName, arguments, ct).ConfigureAwait(false);
            var tail = result.Output.Length > 600
                ? "…" + result.Output[^600..]
                : result.Output;
            return (result.ExitCode, tail.Trim());
        }
        catch (TimeoutException exception)
        {
            _logger.Warn("USCRIPT | run timeout | " + data.Id + " | " + exception.Message);
            return (-1, L.T(exception.Message));
        }
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

    // Лимит пользовательского скрипта (аудит): без таймаута бесконечный цикл
    // в скрипте держал IsRunning=true вечно и оставлял elevated-процесс после
    // выхода из приложения. Таймаут ставит CTS.CancelAfter — зарегистрированный
    // киллер гасит дерево процессов.
    internal static readonly TimeSpan ScriptRunTimeout = TimeSpan.FromMinutes(15);

    private static async Task<(int ExitCode, string Output)> RunCaptureAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(ScriptRunTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var runToken = linked.Token;

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

        // Отмена/таймаут убивают дерево процессов (аудит п. 7): powershell/cmd и
        // их дочерние процессы не продолжают жить после отмены в UI.
        using var registration = runToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // процесс мог выйти между проверкой и Kill — не ошибка
            }
        });

        // Чтение без ct: при отмене пайпы закроются вместе с убитым деревом,
        // задачи чтения завершатся сами ( faulted-чтение ждёт ObserveQuietly).
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(runToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await RunnerGuard.ObserveQuietly(stdout, stderr).ConfigureAwait(false);
            throw new TimeoutException($"Скрипт не завершился за {ScriptRunTimeout.TotalMinutes:F0} мин — процесс принудительно снят.");
        }
        catch (OperationCanceledException)
        {
            await RunnerGuard.ObserveQuietly(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        return (process.ExitCode, output);
    }
}
