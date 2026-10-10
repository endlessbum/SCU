using SCU.Infrastructure.Logging;
using SCU.Infrastructure.Storage;
using SCU.Interop;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// Аудит UI-состояний сканера: отмена до старта ScannerCore (ожидание gate)
// обязана давать тот же итоговый badge «Прервано», что и отмена внутри
// сканера — раньше badge оставался пустым.
public sealed class ScannerCancellationStateTests : IDisposable
{
    private readonly string _directory;
    private readonly Logger _logger;

    public ScannerCancellationStateTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logger = Logger.CreateForCurrentRun();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична.
        }
    }

    private ScannerViewModel CreateViewModel() =>
        new(
            _logger,
            new ScannerRunner(_logger),
            new HistoryStore(_logger, Path.Combine(_directory, "scanner-history.json")));

    [Fact]
    public async Task CancelWhileWaitingGate_SetsCancelledBadge()
    {
        var vm = CreateViewModel();
        var target = Path.Combine(_directory, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "sample.txt"), "clean");

        // Занимаем gate: RunScanAsync зависнет на WaitAsync до старта ScannerCore.
        await vm._scannerGate.WaitAsync();
        try
        {
            var scanTask = vm.HandleDroppedPathsAsync([target]);
            await Task.Delay(100);
            Assert.True(vm.IsBusy);

            vm.CancelOngoing();
            await scanTask;

            Assert.Equal("Cancelled", vm.OutcomeBadgeKind);
            Assert.False(string.IsNullOrEmpty(vm.OutcomeBadge));
            Assert.False(vm.IsBusy);
        }
        finally
        {
            vm._scannerGate.Release();
        }
    }

    // Сценарий «отмена во время перечисления целей» детерминированно не
    // воспроизводится (перечисление заканчивается быстрее любых задержек) —
    // его закрывает отмена внутри ScannerCore из интеграционных тестов;
    // здесь зафиксирован шаг до старта сканера (ожидание gate).
}
