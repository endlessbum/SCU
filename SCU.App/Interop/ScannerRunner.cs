using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using SCU.Common;
using SCU.Models.Scan;

namespace SCU.Interop;

// Раннер ScannerCore.exe по образцу SCURunner (документ п. 3/39): отдельный
// процесс, аргументы строго через ArgumentList, построчное чтение stdout через
// ConsoleOutputDecoder, отмена — Kill(entireProcessTree: true).
// События протокола парсит ScanEventParser; итог — Result<ScanResultDto>.

public sealed class ScannerRunner
{
    // Пин сертификата подписи ScannerCore (генерируется csproj-таргетом из
    // SecurityScanner\signing\thumbprint.txt; пустая строка = dev-сборка).
    private static string SigningPin => ScannerSigningPin.Thumbprint;

    private static bool HasSigningPin => SigningPin.Length == 40;

    private readonly Logger _logger;
    private readonly string _scannerPath;

    public ScannerRunner(Logger logger)
    {
        _logger = logger;
        _scannerPath = Path.Combine(AppContext.BaseDirectory, "ScannerCore.exe");
    }

    public bool IsAvailable => File.Exists(_scannerPath);

    // Пин-проверка ScannerCore перед запуском (п. 48: предотвращение подмены).
    // 1) WinVerifyTrust: хэш подписи соответствует содержимому — файл со
    //    скопированным сертификатом не проходит (CreateFromSignedFile его бы пропустил).
    //    Отказ «корень не доверенный» допускается (п. SEC-01 аудита): release
    //    подписывается self-signed сертификатом, C++-сторона pin-режима также
    //    не требует доверенного корня; дайджест-ошибки остаются фатальными.
    // 2) Точное совпадение отпечатка сертификата подписанта с пином.
    // В dev-сборках (без пина) пропускается.
    private Result VerifyScannerPin()
    {
        if (!HasSigningPin)
        {
            return Result.Success();
        }

        try
        {
            var integrity = SignatureVerifier.VerifyPinnedBinaryIntegrity(_scannerPath);
            if (!integrity.IsSuccess)
            {
                _logger.Error("SCAN | pin check | ScannerCore failed WinVerifyTrust | " + integrity.Message);
                return Result.Failure("ScannerCore.exe: проверка подписи не пройдена — запуск запрещён.", 98);
            }

            // SYSLIB0057: замены нет — X509CertificateLoader не умеет извлекать
            // сертификат подписанта из PE-файла, а пин-проверка опирается именно
            // на это (Authenticode-подпись ScannerCore.exe).
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(_scannerPath);
#pragma warning restore SYSLIB0057
            if (string.Equals(certificate.GetCertHashString(), SigningPin, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success();
            }

            _logger.Error("SCAN | pin mismatch | ScannerCore certificate does not match the pinned thumbprint");
            return Result.Failure("ScannerCore.exe подписан неизвестным сертификатом — запуск запрещён.", 98);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            _logger.Error("SCAN | pin check | ScannerCore.exe is not signed, but a signing pin is configured");
            return Result.Failure("ScannerCore.exe не подписан — запуск запрещён.", 98);
        }
    }

    // Разобранное состояние одного запуска сканера (stdout-события).
    private sealed class RunContext
    {
        public ScanResultDto? Result { get; set; }
        public bool HadProtocolError { get; set; }
        public string DbVersion { get; set; } = string.Empty;
        public string DbDate { get; set; } = string.Empty;
        public string UpdateStatus { get; set; } = string.Empty;
        public string UpdateError { get; set; } = string.Empty;
        public long UpdateEntries { get; set; }
    }

    public async Task<Result<ScanResultDto>> RunScanAsync(
        string path,
        bool scanDirectory,
        IProgress<ScanEvent>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return Result<ScanResultDto>.Failure("ScannerCore.exe не найден.", 2);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<ScanResultDto>.Failure("Не задан путь сканирования.");
        }

        var pinError = VerifyScannerPin();
        if (!pinError.IsSuccess)
        {
            return Result<ScanResultDto>.Failure(pinError.Message, pinError.Code);
        }

        using var process = new Process();
        process.StartInfo = BuildStartInfo(path, scanDirectory);
        process.EnableRaisingEvents = true;

        _logger.Info($"SCAN | start | path={path} | mode={(scanDirectory ? "custom" : "file")}");

        var context = new RunContext();
        var runError = await RunProcessAsync(
            process,
            context,
            scanEvent => progress?.Report(scanEvent),
            ct).ConfigureAwait(false);
        if (runError is not null)
        {
            return Result<ScanResultDto>.Failure(runError.Value.Message, runError.Value.Code);
        }

        // Событие finished — единственный источник итога (документ п. 6: 14 шагов).
        if (context.Result is not null)
        {
            return Result<ScanResultDto>.Success(
                context.Result,
                context.Result.Summary.Detections > 0
                    ? $"Обнаружений: {context.Result.Summary.Detections}"
                    : "Обнаружений не найдено.");
        }

        // Без finished — сканер упал до отчёта: ошибка ≠ Clean (документ п. 36).
        var code = process.ExitCode;
        _logger.Error($"SCAN | no finished event | rc={code} | protocolError={context.HadProtocolError}");
        return Result<ScanResultDto>.Failure(
            "ScannerCore завершился без итогового отчёта.",
            code != 0 ? code : 99);
    }

    // Offline-установка подписанного пакета базы (п. 30/31).
    public async Task<Result<string>> RunUpdateAsync(string packagePath, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return Result<string>.Failure("ScannerCore.exe не найден.", 2);
        }

        if (!File.Exists(packagePath))
        {
            return Result<string>.Failure("Пакет базы не найден.", 2);
        }

        var pinError = VerifyScannerPin();
        if (!pinError.IsSuccess)
        {
            return Result<string>.Failure(pinError.Message, pinError.Code);
        }

        using var process = new Process();
        var psi = new ProcessStartInfo
        {
            FileName = _scannerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        psi.ArgumentList.Add("update");
        psi.ArgumentList.Add("--package");
        psi.ArgumentList.Add(packagePath);
        if (!HasSigningPin)
        {
            psi.ArgumentList.Add("--dev-unsigned-ok");
        }
        process.StartInfo = psi;

        _logger.Info($"DBUPDATE | start | {packagePath}");

        var context = new RunContext();
        var runError = await RunProcessAsync(process, context, onEvent: null, ct).ConfigureAwait(false);
        if (runError is not null)
        {
            return Result<string>.Failure(runError.Value.Message, runError.Value.Code);
        }

        if (context.UpdateStatus == "ok")
        {
            var message = $"База обновлена: {context.DbVersion} (записей: {context.UpdateEntries})";
            _logger.Info("DBUPDATE | ok | " + message);
            return Result<string>.Success(message, message);
        }

        // Незнакомый исход: обновление не применилось.
        var fail = context.UpdateError is { Length: > 0 } ? context.UpdateError : "обновление не выполнено";
        _logger.Error("DBUPDATE | failed | " + fail);
        return Result<string>.Failure("Не удалось установить пакет базы: " + fail,
            process.ExitCode != 0 ? process.ExitCode : 1);
    }

    // Общая часть запуска ScannerCore: отмена, чтение stdout/stderr, парсинг событий.
    // Событие парсится один раз здесь: и итог (RunContext), и подписчик progress
    // получают один и тот же разобранный объект — повторный парсинг строки в VM не нужен.
    private async Task<(string Message, int Code)?> RunProcessAsync(
        Process process,
        RunContext context,
        Action<ScanEvent>? onEvent,
        CancellationToken ct)
    {
        try
        {
            if (!process.Start())
            {
                return ("Не удалось запустить ScannerCore.", 1);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("SCAN | start failed | " + exception.Message);
            return ("Не удалось запустить ScannerCore: " + exception.Message, 1);
        }

        // Таймаут комбинируется с пользовательской отменой; зависший сканер
        // (сломанный WMI, огромная директория на HDD) не висит вечно.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RunnerGuard.DefaultTimeout);
        var effectiveCt = timeoutCts.Token;

        using var registration = effectiveCt.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.Warn("CANCEL | scan | kill process tree");
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
                var scanEvent = ScanEventParser.Parse(line);
                HandleEventLine(scanEvent, context);
                onEvent?.Invoke(scanEvent);
            },
            effectiveCt);
        var stderrTask = ConsoleOutputDecoder.ReadLinesAsync(
            process.StandardError.BaseStream,
            line => _logger.Error(line),
            effectiveCt);

        try
        {
            await process.WaitForExitAsync(effectiveCt).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.Warn("CANCEL | scan");
            await RunnerGuard.ObserveQuietly(stdoutTask, stderrTask).ConfigureAwait(false);
            return ("Отменено", -1);
        }
        catch (OperationCanceledException)
        {
            _logger.Error($"TIMEOUT | scan | {RunnerGuard.DefaultTimeout}");
            await RunnerGuard.ObserveQuietly(stdoutTask, stderrTask).ConfigureAwait(false);
            return ($"Превышено время ожидания сканирования ({RunnerGuard.DefaultTimeout.TotalMinutes:F0} мин) — процесс принудительно завершён.", -2);
        }
        catch (InvalidOperationException exception) when (ct.IsCancellationRequested)
        {
            _logger.Warn("CANCEL | scan | " + exception.Message);
            await RunnerGuard.ObserveQuietly(stdoutTask, stderrTask).ConfigureAwait(false);
            return ("Отменено", -1);
        }
        catch (Exception exception)
        {
            _logger.Error("SCAN | wait failed | " + exception.Message);
            return ("Ошибка ожидания ScannerCore: " + exception.Message, 1);
        }

        if (ct.IsCancellationRequested)
        {
            _logger.Warn("CANCEL | scan");
            return ("Отменено", -1);
        }

        return null;
    }

    private void HandleEventLine(ScanEvent scanEvent, RunContext context)
    {
        switch (scanEvent.Kind)
        {
            case ScanEventKind.Started:
                context.DbVersion = scanEvent.DbVersion;
                context.DbDate = scanEvent.DbDate;
                break;
            case ScanEventKind.Warning:
                _logger.Warn("SCAN | " + scanEvent.Message);
                break;
            case ScanEventKind.Error:
                context.HadProtocolError = true;
                _logger.Error("SCAN | " + scanEvent.Message);
                break;
            case ScanEventKind.Update:
                context.UpdateStatus = scanEvent.UpdateStatus;
                context.UpdateError = scanEvent.Message;
                context.DbVersion = scanEvent.DbVersion;
                context.UpdateEntries = scanEvent.UpdateEntries;
                break;
            case ScanEventKind.Finished:
                context.Result = scanEvent.Result ?? new ScanResultDto();
                break;
        }
    }

    private ProcessStartInfo BuildStartInfo(string path, bool scanDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _scannerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Рабочая директория — НЕ директория сканирования (документ п. 23):
            // защита от DLL hijacking через scan path.
            WorkingDirectory = AppContext.BaseDirectory
        };

        psi.ArgumentList.Add("scan");
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(scanDirectory ? "custom" : "file");
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(path);

        // Флаг dev-сборки передаётся только когда пина нет; при активном пине
        // ScannerCore сам требует корректную подпись (strict, fail-closed).
        if (!HasSigningPin)
        {
            psi.ArgumentList.Add("--dev-unsigned-ok");
        }

        return psi;
    }
}
