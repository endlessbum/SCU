using SCU.Common;
using SCU.Interop;

namespace SCU.Infrastructure.Windows.Drivers;

// Резервный экспорт сторонних пакетов драйверов: pnputil /export-driver в папку,
// выбранную пользователем. pnputil запускается абсолютным путём из System32
// (SystemTool.Path — защита от binary planting), таймаут и kill-tree — в раннере.
public sealed class DriverExportService
{
    private readonly LongProcessRunner _runner;
    private readonly Logger _logger;

    public DriverExportService(Logger logger)
    {
        _logger = logger;
        _runner = new LongProcessRunner(logger);
    }

    public async Task<Result<int>> ExportAsync(
        string targetDirectory,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return Result<int>.Failure("Не выбрана папка экспорта.", 2);
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception exception)
        {
            return Result<int>.Failure(L.T("Не удалось создать папку: {0}", exception.Message));
        }

        // Современный pnputil (Win10 1607+/Win11) принимает "/export-driver <папка>";
        // старые сборки требовали "*". Пробуем документированную форму, при отказе — legacy.
        progress?.Report(L.T("Экспорт пакетов драйверов…"));
        var result = await _runner
            .RunAsync("pnputil.exe", ["/export-driver", targetDirectory], progress, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.Warn("DRV | export modern form failed | rc=" + result.Code + " | retry with '*'");
            result = await _runner
                .RunAsync("pnputil.exe", ["/export-driver", "*", targetDirectory], progress, ct)
                .ConfigureAwait(false);
        }

        if (!result.IsSuccess)
        {
            return Result<int>.Failure(
                L.T("pnputil завершился с ошибкой (код {0}): {1}", result.Code, result.Message), result.Code);
        }

        var packages = CountPackages(targetDirectory);
        _logger.Info($"DRV | export ok | {packages} packages -> {targetDirectory}");
        return Result<int>.Success(packages, result.Message);
    }

    private static int CountPackages(string targetDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(targetDirectory, "*.inf", SearchOption.AllDirectories).Count();
        }
        catch
        {
            // Подсчёт не критичен: экспорт мог пройти успешно.
            return 0;
        }
    }
}
