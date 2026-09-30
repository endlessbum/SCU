using SCU.Models;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Исполнение проверок: probes → DiagnosticContext → правила → находки.
/// Ошибка одного probe не останавливает запуск (п. 14 плана).
/// </summary>
public sealed class DiagnosticEngine
{
    private readonly Logger _logger;

    public DiagnosticEngine(Logger logger, IReadOnlyList<DiagnosticProbe> probes)
    {
        _logger = logger;
        Probes = probes;
    }

    public IReadOnlyList<DiagnosticProbe> Probes { get; }

    /// <summary>
    /// Полный запуск: все probes по группам (лёгкие параллельно, тяжёлые после).
    /// rows — строки прогресса по индексам Probes.
    /// </summary>
    public async Task RunAsync(DiagnosticContext context, IReadOnlyList<ProbeStatusRow> rows, CancellationToken ct)
    {
        // Группа 1 — быстрое чтение состояния (WMI/реестр/ServiceController).
        // Группа 2 — тяжёлое (журнал событий, DISM): не конкурирует с первой.
        var fast = Probes.Where(probe => probe is not EventLogProbe and not SystemFileProbe).ToList();
        var heavy = Probes.Where(probe => probe is EventLogProbe or SystemFileProbe).ToList();

        await RunGroupAsync(context, rows, fast, ct).ConfigureAwait(false);
        await RunGroupAsync(context, rows, heavy, ct).ConfigureAwait(false);
    }

    /// <summary>Повторный запуск одного probe (verify после исправления).</summary>
    public async Task<bool> RunProbeAsync(
        string probeId, DiagnosticContext context, IReadOnlyList<ProbeStatusRow> rows, CancellationToken ct)
    {
        var probe = Probes.FirstOrDefault(candidate => candidate.Id == probeId);
        if (probe is null)
        {
            return false;
        }

        var index = IndexOf(probe);
        var row = index >= 0 && index < rows.Count ? rows[index] : null;
        return await RunOneAsync(probe, row, context, ct).ConfigureAwait(false);
    }

    private async Task RunGroupAsync(
        DiagnosticContext context, IReadOnlyList<ProbeStatusRow> rows, IReadOnlyList<DiagnosticProbe> probes, CancellationToken ct)
    {
        var tasks = probes.Select(probe =>
        {
            var index = IndexOf(probe);
            var row = index >= 0 && index < rows.Count ? rows[index] : null;
            return RunOneAsync(probe, row, context, ct);
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private int IndexOf(DiagnosticProbe probe)
    {
        for (var index = 0; index < Probes.Count; index++)
        {
            if (ReferenceEquals(Probes[index], probe))
            {
                return index;
            }
        }

        return -1;
    }

    private async Task<bool> RunOneAsync(
        DiagnosticProbe probe, ProbeStatusRow? row, DiagnosticContext context, CancellationToken ct)
    {
        row?.State = ProbeState.Running;
        _logger.Info("TROUBLESHOOT | PROBE | id=" + probe.Id + " | started");
        try
        {
            await probe.CollectAsync(context, ct).ConfigureAwait(false);
            row?.State = ProbeState.Ok;
            _logger.Info("TROUBLESHOOT | PROBE | id=" + probe.Id + " | success");
            return true;
        }
        catch (OperationCanceledException)
        {
            row?.State = ProbeState.Pending;
            _logger.Warn("TROUBLESHOOT | PROBE | id=" + probe.Id + " | cancelled");
            return false;
        }
        catch (Exception exception)
        {
            row?.State = ProbeState.Failed;
            _logger.Error("TROUBLESHOOT | PROBE | id=" + probe.Id + " | " + exception.Message);
            return false;
        }
    }
}
