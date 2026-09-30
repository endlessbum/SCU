using System.Diagnostics;
using SCU.Common;

namespace SCU.Interop;

public sealed class SCURunner
{
    private readonly Logger _logger;
    private readonly string _scriptPath;

    public SCURunner(Logger logger)
    {
        _logger = logger;
        _scriptPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SCU.ps1");
    }

    public bool IsAvailable => File.Exists(_scriptPath);

    public async Task<Result> RunAsync(
        string action,
        IReadOnlyDictionary<string, string?>? args = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return Result.Failure("SCU.ps1 не найден.", 2);
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            return Result.Failure("Не задан Action.");
        }

        using var process = new Process();
        process.StartInfo = BuildStartInfo(action, args);
        process.EnableRaisingEvents = true;

        _logger.Info($"PS | start | action={action}");

        try
        {
            if (!process.Start())
            {
                return Result.Failure("Не удалось запустить PowerShell.");
            }
        }
        catch (Exception exception)
        {
            _logger.Error("PS | start failed | " + exception.Message);
            return Result.Failure("Не удалось запустить SCU.ps1: " + exception.Message);
        }

        // Таймаут комбинируется с пользовательской отменой: сработавший таймаут
        // тоже убивает дерево процессов через registration.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RunnerGuard.PowerShellTimeout);
        var effectiveCt = timeoutCts.Token;

        using var registration = effectiveCt.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.Warn($"CANCEL | action={action} | kill process tree");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception)
            {
                _logger.Error("CANCEL | kill failed | " + exception.Message);
            }
        });

        var stdoutTask = ConsoleOutputDecoder.ReadLinesAsync(
            process.StandardOutput.BaseStream,
            line =>
            {
                _logger.Raw(line);
                progress?.Report(line);
            },
            effectiveCt);
        var stderrTask = ConsoleOutputDecoder.ReadLinesAsync(
            process.StandardError.BaseStream,
            line =>
            {
                _logger.Error(line);
                progress?.Report(line);
            },
            effectiveCt);

        try
        {
            await process.WaitForExitAsync(effectiveCt).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.Warn($"CANCEL | action={action}");
            await RunnerGuard.ObserveQuietly(stdoutTask, stderrTask).ConfigureAwait(false);
            return Result.Failure("Отменено", -1);
        }
        catch (OperationCanceledException)
        {
            // Таймаут, а не пользовательская отмена: процесс убит registration-ом.
            _logger.Error($"TIMEOUT | action={action} | {RunnerGuard.PowerShellTimeout}");
            await RunnerGuard.ObserveQuietly(stdoutTask, stderrTask).ConfigureAwait(false);
            return Result.Failure(
                $"Превышено время ожидания операции ({RunnerGuard.PowerShellTimeout.TotalMinutes:F0} мин) — процесс принудительно завершён.", -2);
        }
        catch (InvalidOperationException exception) when (ct.IsCancellationRequested)
        {
            _logger.Warn($"CANCEL | action={action} | {exception.Message}");
            await RunnerGuard.ObserveQuietly(stdoutTask, stderrTask).ConfigureAwait(false);
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            _logger.Error($"PS | wait failed | action={action} | {exception.Message}");
            return Result.Failure("Ошибка ожидания SCU.ps1: " + exception.Message);
        }

        if (ct.IsCancellationRequested)
        {
            _logger.Warn($"CANCEL | action={action}");
            return Result.Failure("Отменено", -1);
        }

        return MapExitCode(process.ExitCode);
    }

    private ProcessStartInfo BuildStartInfo(
        string action,
        IReadOnlyDictionary<string, string?>? args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NoLogo");
        // Зависший в скрипте Read-Host держал бы процесс вечно без видимого окна.
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(_scriptPath);
        psi.ArgumentList.Add("-Action");
        psi.ArgumentList.Add(action);

        if (args is not null)
        {
            foreach (var pair in args)
            {
                psi.ArgumentList.Add("-" + pair.Key);
                if (pair.Value is not null)
                {
                    psi.ArgumentList.Add(pair.Value);
                }
            }
        }

        psi.Environment["SCU_LOGFILE"] = _logger.FilePath;
        return psi;
    }

    private static string ResolvePowerShellPath()
    {
        var systemPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        // Fallback на голое имя убран: он включал в поиск CreateProcess каталог
        // приложения — подменённый powershell.exe выполнился бы с правами SCU.
        // Нет System32-файла — честный отказ запуска.
        return File.Exists(systemPath)
            ? systemPath
            : throw new FileNotFoundException("powershell.exe не найден в System32 (WindowsPowerShell\\v1.0).");
    }

    private static Result MapExitCode(int code) => code switch
    {
        0 => Result.Success("SCU выполнен успешно."),
        1 => Result.Failure("частичная ошибка", 1),
        2 => Result.Failure("не найден входной файл", 2),
        99 => Result.Failure("фатальная ошибка скрипта", 99),
        _ => Result.Failure($"код {code}", code)
    };
}
