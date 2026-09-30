using System.Management;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Драйверы и устройства (п. 7.8 плана): устройства с ошибками загрузки
/// драйверов и problem codes через Win32_PnPEntity. Показывает устройство,
/// производителя и код проблемы; автоматическая установка драйверов не выполняется.
/// </summary>
public sealed class DriverProbe : DiagnosticProbe
{
    public override string Id => "drivers";

    public override string Title => "Проверка драйверов";

    public override Task CollectAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var problems = new List<DriverProblemSnapshot>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, Manufacturer, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode > 0");
        using var results = searcher.Get();
        foreach (var item in results.OfType<ManagementObject>())
        {
            ct.ThrowIfCancellationRequested();
            using (item)
            {
                try
                {
                    var code = Convert.ToUInt32(item["ConfigManagerErrorCode"]);
                    if (code == 0)
                    {
                        continue;
                    }

                    problems.Add(new DriverProblemSnapshot(
                        item["Name"]?.ToString()?.Trim() ?? string.Empty,
                        item["DeviceID"]?.ToString() ?? string.Empty,
                        item["Manufacturer"]?.ToString()?.Trim() ?? string.Empty,
                        code));
                }
                catch
                {
                    // Нечитаемое устройство пропускаем — остальные продолжают сбор.
                }
            }
        }

        context.DriverProblems = problems;
    }, CancellationToken.None);
}
