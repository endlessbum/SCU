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
            FileName = fileName,
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

        using var registration = ct.Register(() =>
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
        }, ct);
        var stderrTask = ConsoleOutputDecoder.ReadLinesAsync(process.StandardError.BaseStream, line =>
        {
            lock (stderrLines)
            {
                stderrLines.Add(line);
            }

            _logger.Error(line);
            progress?.Report(line);
        }, ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"CANCEL | {fileName}");
            return Result<string>.Failure("Отменено", -1);
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
