using System.Management;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Состояние накопителей: предсказание отказа через SMART
/// (root\wmi MSStorageDriver_FailurePredictStatus — стабильный API без
/// парсинга локализованного текста). Диски без поддержки SMART пропускаются.
/// </summary>
public sealed class StorageHealthProbe : DiagnosticProbe
{
    public override string Id => "storage";

    public override string Title => "Проверка накопителей";

    public override Task CollectAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var failures = new List<SmartFailureSnapshot>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT InstanceName FROM MSStorageDriver_FailurePredictStatus WHERE PredictFailure = TRUE");
            using var results = searcher.Get();
            foreach (var item in results.OfType<ManagementObject>())
            {
                ct.ThrowIfCancellationRequested();
                using (item)
                {
                    var name = item["InstanceName"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        failures.Add(new SmartFailureSnapshot(name));
                    }
                }
            }
        }
        catch
        {
            // Нет поддержки WMI/SMART у железа или нет доступа — признак остаётся
            // неизвестным, правило не срабатывает (без ложных находок).
        }

        context.SmartFailures = failures;
    }, CancellationToken.None);
}
