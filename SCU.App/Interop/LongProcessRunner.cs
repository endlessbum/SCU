using System.Diagnostics;
using System.Text.RegularExpressions;
using SCU.Common;

namespace SCU.Interop;

// Общий раннер внешних процессов (powercfg, bcdedit, netsh, nbtstat) —
// как SCURunner, но с произвольным файлом и аргументами через ArgumentList.
// Кодировка вывода — построчная эвристика из ConsoleOutputDecoder.
public sealed class LongProcessRunner
{
    private readonly Logger _logger;

    public LongProcessRunner(Logger logger)
    {
        _logger = logger;
    }

    public async Task<Result<string>> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            // Голое имя ("netsh", "powercfg") включает в поиск CreateProcess каталог
            // приложения и текущий каталог — binary planting с правами SCU.
            // Известная системная утилита всегда запускается абсолютным путём.
            FileName = SystemTool.Path(fileName),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = new Process();
        process.StartInfo = psi;

        _logger.Info($"PROC | start | {fileName} {string.Join(' ', arguments)}");

        try
        {
            if (!process.Start())
            {
                return Result<string>.Failure("Не удалось запустить процесс.", 1);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("PROC | start failed | " + exception.Message);
            return Result<string>.Failure($"Не удалось запустить {fileName}: {exception.Message}");
        }

        // Таймаут комбинируется с пользовательской отменой; зависший powercfg/DISM
        // не должен блокировать вкладку навсегда.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RunnerGuard.DefaultTimeout);
        var effectiveCt = timeoutCts.Token;

        using var registration = effectiveCt.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.Warn($"CANCEL | {fileName} | kill process tree");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception)
            {
                _logger.Error("CANCEL | kill failed | " + exception.Message);
            }
        });

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var stdoutTask = ConsoleOutputDecoder.ReadLinesAsync(process.StandardOutput.BaseStream, line =>
        {
            lock (stdoutLines)
            {
                stdoutLines.Add(line);
            }

            _logger.Raw(line);
            progress?.Report(line);
        }, effectiveCt);
        var stderrTask = ConsoleOutputDecoder.ReadLinesAsync(process.StandardError.BaseStream, line =>
        {
            lock (stderrLines)
            {
                stderrLines.Add(line);
            }

            _logger.Error(line);
            progress?.Report(line);
        }, effectiveCt);

        try
        {
            await process.WaitForExitAsync(effectiveCt).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.Warn($"CANCEL | {fileName}");
            await RunnerGuard.ObserveQuietly(stdoutTask, stderrTask).ConfigureAwait(false);
            return Result<string>.Failure("Отменено", -1);
        }
        catch (OperationCanceledException)
        {
            _logger.Error($"TIMEOUT | {fileName} | {RunnerGuard.DefaultTimeout}");
            await RunnerGuard.ObserveQuietly(stdoutTask, stderrTask).ConfigureAwait(false);
            return Result<string>.Failure(
                $"Превышено время ожидания {fileName} ({RunnerGuard.DefaultTimeout.TotalMinutes:F0} мин) — процесс принудительно завершён.", -2);
        }
        catch (Exception exception)
        {
            _logger.Error($"PROC | wait failed | {exception.Message}");
            return Result<string>.Failure($"Ошибка ожидания {fileName}: {exception.Message}");
        }

        if (ct.IsCancellationRequested)
        {
            return Result<string>.Failure("Отменено", -1);
        }

        lock (stdoutLines)
        {
            var code = process.ExitCode;
            var output = string.Join(Environment.NewLine, stdoutLines);
            if (code == 0)
            {
                return Result<string>.Success(output);
            }

            // Причина сбоя DISM/bcdedit/powercfg обычно в stderr — включаем её в
            // сообщение, иначе пользователь видит только «код N».
            lock (stderrLines)
            {
                var stderr = string.Join(
                    " | ",
                    stderrLines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()));
                return Result<string>.Failure(
                    stderr.Length > 0 ? $"{fileName}: код {code}: {stderr}" : $"{fileName}: код {code}",
                    code);
            }
        }
    }

    // Поиск GUID-ов в выводе powercfg — не зависит от локализации.
    public static IReadOnlyList<string> ExtractGuids(string output)
    {
        var matches = Regex.Matches(
            output,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return matches.Select(match => match.Value).ToList();
    }
}
