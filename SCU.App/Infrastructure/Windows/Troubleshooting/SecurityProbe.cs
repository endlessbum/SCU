using System.Management;
using Microsoft.Win32;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Безопасность: UAC (те же величины, что читает раздел 13) и антивирусная защита
/// из SecurityCenter2. Пользовательская настройка не считается ошибкой —
/// оценивают правила (Defender real-time off — предупреждение, остальное Info).
/// </summary>
public sealed class SecurityProbe : DiagnosticProbe
{
    public override string Id => "security";

    public override string Title => "Проверка безопасности";

    public override Task CollectAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            var psd = key?.GetValue("PromptOnSecureDesktop") as int? ?? 1;
            var cpba = key?.GetValue("ConsentPromptBehaviorAdmin") as int? ?? 5;
            context.UacStandard = psd == 1 && cpba == 5;
        }
        catch
        {
            context.UacStandard = null;
        }

        var (registered, realTimeOn) = ReadSecurityCenter("AntiVirusProduct");
        context.AntivirusRegistered = registered;
        context.AntivirusRealTimeOn = realTimeOn;
    }, CancellationToken.None);

    // productState — недокументированное поле; эвристика «бит 0x1000 = включено»
    // общепринята, но не гарантируется: при нечитаемом состоянии возвращаем null.
    private static (bool? Registered, bool? RealTimeOn) ReadSecurityCenter(string className)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", $"SELECT productState FROM {className}");
            using var results = searcher.Get();
            var any = false;
            var anyEnabled = false;
            foreach (var item in results.OfType<ManagementObject>())
            {
                using (item)
                {
                    any = true;
                    if (item["productState"] is uint state && (state & 0x1000) != 0)
                    {
                        anyEnabled = true;
                    }
                }
            }

            return any
                ? (true, anyEnabled)
                : ((bool?)null, (bool?)null);
        }
        catch
        {
            return (null, null);
        }
    }
}
