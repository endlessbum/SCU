using System.ServiceProcess;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Оркестрация вкладки «Устранение неполадок»: полный запуск, повторная проверка
/// одной области (fix → verify) и исполнение подтверждённых исправлений.
/// </summary>
public sealed class TroubleshootingService
{
    private readonly Logger _logger;
    private readonly DiagnosticEngine _engine;
    private readonly NetworkService _networkService;
    private readonly MaintenanceService _maintenanceService;
    private readonly ServiceManager _serviceManager;

    public TroubleshootingService(Logger logger, LongProcessRunner runner)
    {
        _logger = logger;
        _engine = new DiagnosticEngine(logger,
        [
            new SystemProbe(),
            new DiskProbe(),
            new ServicesProbe(new ServiceManager()),
            new UpdateProbe(),
            new NetworkProbe(),
            new StartupProbe(),
            new SecurityProbe(),
            new EventLogProbe(),
            new SystemFileProbe(runner),
            new DriverProbe(),
            new StorageHealthProbe(),
            new AudioProbe()
        ]);
        _networkService = new NetworkService(logger, runner);
        _maintenanceService = new MaintenanceService(logger, runner);
        _serviceManager = new ServiceManager();
    }

    /// <summary>Probe-строки для UI идут в том же порядке, что и probes.</summary>
    public IReadOnlyList<DiagnosticProbe> Probes => _engine.Probes;

    /// <summary>Контекст последнего запуска: для повторной проверки после исправления.</summary>
    public DiagnosticContext CurrentContext { get; private set; } = new();

    /// <summary>Полный запуск диагностики: probes → правила → находки.</summary>
    public async Task<List<DiagnosticFinding>> RunFullAsync(IReadOnlyList<ProbeStatusRow> rows, CancellationToken ct)
    {
        var context = new DiagnosticContext();
        CurrentContext = context;
        await _engine.RunAsync(context, rows, ct).ConfigureAwait(false);
        var findings = DiagnosticRules.Evaluate(context);
        foreach (var finding in findings)
        {
            _logger.Info("TROUBLESHOOT | FINDING | id=" + finding.Id + " | severity=" + finding.Severity);
        }

        return findings;
    }

    /// <summary>
    /// Повторная проверка после исправления: перезапуск затронутых probes и
    /// переоценка всех правил по обновлённому контексту.
    /// </summary>
    public async Task<List<DiagnosticFinding>> RecheckAsync(IEnumerable<string> probeIds, IReadOnlyList<ProbeStatusRow> rows, CancellationToken ct)
    {
        foreach (var probeId in probeIds)
        {
            await _engine.RunProbeAsync(probeId, CurrentContext, rows, ct).ConfigureAwait(false);
        }

        return DiagnosticRules.Evaluate(CurrentContext);
    }

    /// <summary>Исполнение исправления из карточки. Диагностика вызывает только после подтверждения.</summary>
    public async Task<Result> RunFixAsync(DiagnosticAction action, CancellationToken ct)
    {
        _logger.Info("TROUBLESHOOT | FIX | id=" + action.FixId + " | started");
        var result = action.FixId switch
        {
            "flush_dns" => await _networkService.FlushDnsCacheAsync(ct).ConfigureAwait(false),
            "start_service" => await StartServiceAsync(action.Parameter, ct).ConfigureAwait(false),
            "dism_restore" => await _maintenanceService.RunIntegrityCheckAsync(ct).ConfigureAwait(false),
            _ => Result.Failure("Неизвестное исправление: " + action.FixId)
        };

        _logger.Info("TROUBLESHOOT | FIX | id=" + action.FixId + " | rc=" + result.Code);
        return result;
    }

    // Запуск остановленной службы без изменения типа запуска. Отключённая
    // служба (IsStartAllowed=false) старту не подлежит — сообщаем об этом.
    private async Task<Result> StartServiceAsync(string? serviceName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return Result.Failure("Не задано имя службы.");
        }

        var state = await _serviceManager.GetServiceAsync(serviceName, ct).ConfigureAwait(false);
        if (!state.IsSuccess || state.Value is null)
        {
            return Result.Failure(L.T("Служба {0} не найдена.", serviceName));
        }

        if (state.Value.RuntimeStatus == ServiceRuntimeStatus.Running)
        {
            return Result.Success(L.T("Служба {0} уже работает.", serviceName));
        }

        if (!state.Value.IsStartAllowed)
        {
            return Result.Failure(L.T("Служба {0} отключена — сначала включите её в разделе «Службы Windows».", serviceName));
        }

        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var controller = new ServiceController(serviceName);
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                return Result.Success(L.T("Служба {0} запущена.", serviceName));
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure(L.T("Не удалось запустить службу {0}: {1}", serviceName, exception.Message));
        }
    }
}
