using System.Management;
using Microsoft.Win32;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Базовые сведения об ОС, памяти и признаке ожидающей перезагрузки.
/// Read-only: WMI + реестр.
/// </summary>
public sealed class SystemProbe : DiagnosticProbe
{
    public override string Id => "system";

    public override string Title => "Проверка системы";

    public override Task CollectAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();

        using (var searcher = new ManagementObjectSearcher(
                   "SELECT Caption, LastBootUpTime, FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem"))
        using (var results = searcher.Get())
        {
            foreach (var item in results.OfType<ManagementObject>())
            {
                using (item)
                {
                    context.OsCaption = ReadString(item, "Caption");
                    var boot = ReadBootTime(item);
                    if (boot.HasValue)
                    {
                        context.Uptime = DateTime.Now - boot.Value;
                    }

                    // Величины в КБ.
                    if (ulong.TryParse(ReadString(item, "TotalVisibleMemorySize"), out var totalKb) && totalKb > 0)
                    {
                        context.RamTotalBytes = totalKb * 1024;
                    }

                    if (ulong.TryParse(ReadString(item, "FreePhysicalMemory"), out var freeKb))
                    {
                        context.RamAvailableBytes = freeKb * 1024;
                    }
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        (var pending, var source) = ReadPendingReboot();
        context.PendingReboot = pending;
        context.PendingRebootSource = source;
    }, CancellationToken.None);

    private static DateTime? ReadBootTime(ManagementBaseObject os)
    {
        var raw = ReadString(os, "LastBootUpTime");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(raw);
        }
        catch
        {
            return null;
        }
    }

    // Признаки ожидающей перезагрузки: CBS, Windows Update и PendingFileRenameOperations.
    private static (bool Pending, string? Source) ReadPendingReboot()
    {
        try
        {
            if (Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null)
            {
                return (true, @"Component Based Servicing\RebootPending");
            }

            if (Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null)
            {
                return (true, @"WindowsUpdate\Auto Update\RebootRequired");
            }

            using var sessionManager = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            var renames = sessionManager?.GetValue("PendingFileRenameOperations") as string[];
            if (renames is not null && renames.Any(name => !string.IsNullOrWhiteSpace(name)))
            {
                return (true, "PendingFileRenameOperations");
            }
        }
        catch
        {
            // Нет доступа — признак остаётся неизвестным, правило не срабатывает.
        }

        return (false, null);
    }

    private static string ReadString(ManagementBaseObject obj, string name)
    {
        try
        {
            return obj[name]?.ToString()?.Trim() ?? string.Empty;
        }
        catch (ManagementException)
        {
            return string.Empty;
        }
    }
}
