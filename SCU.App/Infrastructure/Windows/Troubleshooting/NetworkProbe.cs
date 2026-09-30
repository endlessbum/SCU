using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Сетевая диагностика: активный адаптер, шлюз, DNS-разрешение.
/// Не касается ручных настроек TCP/MTU/QoS раздела «Сеть».
/// </summary>
public sealed class NetworkProbe : DiagnosticProbe
{
    private static readonly string DnsProbeHost = "www.msftconnecttest.com";
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);

    public override string Id => "network";

    public override string Title => "Проверка сети";

    public override async Task CollectAsync(DiagnosticContext context, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            var active = adapters.FirstOrDefault(interface_ => interface_.OperationalStatus == OperationalStatus.Up
                && interface_.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && interface_.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

            context.NetworkHasActiveAdapter = active is not null;
            if (active is null)
            {
                context.NetworkHasGateway = false;
                context.NetworkDnsResolves = false;
                return;
            }

            var properties = active.GetIPProperties();
            var gateway = properties.GatewayAddresses
                .FirstOrDefault(gatewayAddress => gatewayAddress.Address.AddressFamily is AddressFamily.InterNetwork
                    or AddressFamily.InterNetworkV6);
            context.NetworkHasGateway = gateway is not null;
            context.NetworkGateway = gateway?.Address.ToString();
        }, ct).ConfigureAwait(false);

        // DNS-разрешение — сеть уже прочитана; таймаут не тянем за собой в сбор прочих областей.
        ct.ThrowIfCancellationRequested();
        context.NetworkDnsTarget = DnsProbeHost;
        context.NetworkDnsResolves = await ResolveAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> ResolveAsync(CancellationToken ct)
    {
        try
        {
            var resolution = Dns.GetHostAddressesAsync(DnsProbeHost, ct);
            var completed = await Task.WhenAny(resolution, Task.Delay(DnsTimeout, ct)).ConfigureAwait(false);
            return completed == resolution
                && resolution.IsCompletedSuccessfully
                && resolution.Result.Length > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
