using SCU.Models;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Состояния ограниченного набора критичных служб (п. 7.5 плана).
/// Сам факт Stopped не равен проблеме — оценивают правила.
/// </summary>
public sealed class ServicesProbe : DiagnosticProbe
{
    // Службы обновления (wuauserv, BITS, UsoSvc) тоже здесь: единый источник состояний.
    // Audiosrv/Spooler/w32time/bthserv/msiserver — области второй очереди (звук,
    // печать, время, Bluetooth, установщик); их правила знают, что остановка
    // demand-start служб (msiserver, bthserv) — нормальное состояние.
    public static IReadOnlyList<string> CriticalServiceNames { get; } =
    [
        "wuauserv", "BITS", "UsoSvc", "WSearch", "eventlog", "Dhcp", "Dnscache",
        "NlaSvc", "nsi", "WinDefend", "wscsvc", "Schedule", "Audiosrv", "Spooler",
        "w32time", "bthserv", "msiserver", "AudioEndpointBuilder"
    ];

    private readonly ServiceManager _serviceManager;

    public ServicesProbe(ServiceManager serviceManager) => _serviceManager = serviceManager;

    public override string Id => "services";

    public override string Title => "Проверка служб";

    public override async Task CollectAsync(DiagnosticContext context, CancellationToken ct)
    {
        var result = await _serviceManager
            .GetServicesAsync(CriticalServiceNames, ct)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
        {
            throw new InvalidOperationException(result.Message);
        }

        context.Services = result.Value
            .Select(info => new ServiceSnapshot(
                info.Name,
                info.DisplayName,
                info.IsStartAllowed,
                info.RuntimeStatus == ServiceRuntimeStatus.Running))
            .ToList();
    }
}
