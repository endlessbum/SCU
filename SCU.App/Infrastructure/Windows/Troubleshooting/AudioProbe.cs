using System.Management;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Аудио (п. 24 плана): присутствующие звуковые устройства через
/// Win32_SoundDevice (стабильный WMI API, без парсинга локализованного текста).
/// Состояния служб звука собирает ServicesProbe; оценивают правила.
/// </summary>
public sealed class AudioProbe : DiagnosticProbe
{
    public override string Id => "audio";

    public override string Title => "Проверка аудио";

    public override Task CollectAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var devices = new List<AudioDeviceSnapshot>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, Manufacturer, Status, ConfigManagerErrorCode FROM Win32_SoundDevice");
        using var results = searcher.Get();
        foreach (var item in results.OfType<ManagementObject>())
        {
            ct.ThrowIfCancellationRequested();
            using (item)
            {
                try
                {
                    devices.Add(new AudioDeviceSnapshot(
                        item["Name"]?.ToString()?.Trim() ?? string.Empty,
                        item["Manufacturer"]?.ToString()?.Trim() ?? string.Empty,
                        item["Status"]?.ToString()?.Trim() ?? string.Empty,
                        Convert.ToUInt32(item["ConfigManagerErrorCode"] ?? (uint)0)));
                }
                catch
                {
                    // Нечитаемое устройство пропускаем — остальные продолжают сбор.
                }
            }
        }

        context.AudioDevices = devices;
    }, CancellationToken.None);
}
