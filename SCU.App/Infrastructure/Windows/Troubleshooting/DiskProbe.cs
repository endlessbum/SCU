using System.Management;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Локальные диски: объём, свободное место, файловая система.
/// chkdsk/S.M.A.R.T. здесь не запускаются — только чтение.
/// </summary>
public sealed class DiskProbe : DiagnosticProbe
{
    public override string Id => "disk";

    public override string Title => "Проверка дисков";

    public override Task CollectAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var disks = new List<DiagnosticDiskSnapshot>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace, DriveType FROM Win32_LogicalDisk");
        using var results = searcher.Get();
        foreach (var item in results.OfType<ManagementObject>())
        {
            ct.ThrowIfCancellationRequested();
            using (item)
            {
                try
                {
                    if (Convert.ToInt64(item["DriveType"]) != 3)
                    {
                        continue;
                    }

                    var letter = item["DeviceID"]?.ToString()?.TrimEnd('\\') ?? string.Empty;
                    if (letter.Length < 2)
                    {
                        continue;
                    }

                    var size = ReadUlong(item, "Size");
                    var free = ReadUlong(item, "FreeSpace");
                    disks.Add(new DiagnosticDiskSnapshot(
                        letter,
                        size,
                        free,
                        IsSystemDrive(letter),
                        item["FileSystem"]?.ToString() ?? string.Empty));
                }
                catch
                {
                    // Нечитаемый диск пропускаем — остальные продолжают сбор.
                }
            }
        }

        context.Disks = disks.OrderBy(d => d.Letter, StringComparer.OrdinalIgnoreCase).ToList();
    }, CancellationToken.None);

    private static bool IsSystemDrive(string letter) =>
        string.Equals(
            Environment.GetFolderPath(Environment.SpecialFolder.System)[..2],
            letter,
            StringComparison.OrdinalIgnoreCase);

    private static ulong ReadUlong(ManagementBaseObject obj, string name) => obj[name] switch
    {
        null => 0,
        ulong u => u,
        uint u => u,
        long l when l >= 0 => (ulong)l,
        int i when i >= 0 => (ulong)i,
        string s when ulong.TryParse(s, out var parsed) => parsed,
        _ => 0
    };
}
