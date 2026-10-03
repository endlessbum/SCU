using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using SCU.Common;
using SCU.Models;

namespace SCU.Infrastructure.Windows.SystemState;

public sealed class SystemInfoService
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableW(
        string name, string guid, IntPtr buffer, uint size);
    public async Task<Result<SystemInfo>> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var info = await Task.Run(() => Read(ct), ct).ConfigureAwait(false);
            return Result<SystemInfo>.Success(info);
        }
        catch (OperationCanceledException)
        {
            return Result<SystemInfo>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result<SystemInfo>.Failure(exception.Message);
        }
    }

    private static SystemInfo Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var fields = new List<InfoRow>();
        var disks = new List<DiskInfo>();
        var video = new List<string>();
        var network = new List<string>();
        var osCaption = string.Empty;
        var osVersion = string.Empty;
        var cpuSummary = string.Empty;
        ulong? ramTotalBytes = null;

        ForEach("SELECT Caption, Version, OSArchitecture, InstallDate, LastBootUpTime, CSName FROM Win32_OperatingSystem", ct, os =>
        {
            fields.Add(new InfoRow("Компьютер", ReadString(os, "CSName")));
            fields.Add(new InfoRow("ОС", ReadString(os, "Caption")));
            fields.Add(new InfoRow("Версия", ReadString(os, "Version")));
            fields.Add(new InfoRow("Архитектура", ReadString(os, "OSArchitecture")));
            fields.Add(new InfoRow("Установлена", ReadCimDate(os, "InstallDate")));
            var boot = ReadCimDateTime(os, "LastBootUpTime");
            fields.Add(new InfoRow("Последняя загрузка", boot is null ? "—" : boot.Value.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)));
            if (boot is not null)
            {
                var uptime = DateTime.Now - boot.Value;
                fields.Add(new InfoRow("Аптайм", FormatUptime(uptime)));
            }

            osCaption = ReadString(os, "Caption");
            osVersion = ReadString(os, "Version");
        });

        ForEach("SELECT Manufacturer, Model, Name, NumberOfProcessors, TotalPhysicalMemory FROM Win32_ComputerSystem", ct, cs =>
        {
            fields.Add(new InfoRow("Производитель", ReadString(cs, "Manufacturer")));
            fields.Add(new InfoRow("Модель", ReadString(cs, "Model")));
            var ram = ReadUInt64(cs, "TotalPhysicalMemory");
            if (ram > 0)
            {
                fields.Add(new InfoRow("ОЗУ (система)", FormatBytes(ram)));
                ramTotalBytes = ram;
            }
        });

        // BIOS: режим прошивки — из реестра (PEFirmwareType: 1 = Legacy, 2 = UEFI;
        // в WMI его нет), версия — SMBIOSBIOSVersion из Win32_BIOS.
        fields.Add(new InfoRow("Режим BIOS", ReadFirmwareMode()));
        ForEach("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", ct, bios =>
        {
            var version = ReadString(bios, "SMBIOSBIOSVersion");
            fields.Add(new InfoRow("Версия BIOS", string.IsNullOrWhiteSpace(version) ? "—" : version));
        });

        // Материнская плата: Product — модель, Version — ревизия платы.
        ForEach("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", ct, board =>
        {
            var product = ReadString(board, "Product");
            var manufacturer = ReadString(board, "Manufacturer");
            var model = product switch
            {
                var p when !string.IsNullOrWhiteSpace(p) => p,
                var m when !string.IsNullOrWhiteSpace(m) => m,
                _ => "—"
            };
            fields.Add(new InfoRow("Модель материнской платы", model));
            var boardVersion = ReadString(board, "Version");
            fields.Add(new InfoRow("Версия материнской платы", string.IsNullOrWhiteSpace(boardVersion) ? "—" : boardVersion));
        });

        var cpuNames = new List<string>();
        var logical = 0;
        ForEach("SELECT Name, NumberOfLogicalProcessors FROM Win32_Processor", ct, cpu =>
        {
            var name = ReadString(cpu, "Name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                cpuNames.Add(name);
            }

            logical += (int)ReadUInt64(cpu, "NumberOfLogicalProcessors");
        });

        if (cpuNames.Count > 0)
        {
            var suffix = logical > 0 ? $" ({logical} лог. ядер)" : string.Empty;
            cpuSummary = string.Join("; ", cpuNames.Distinct()) + suffix;
            fields.Add(new InfoRow("Процессор", cpuSummary));
        }

        ulong dimmSum = 0;
        var dimmCount = 0;
        ForEach("SELECT Capacity FROM Win32_PhysicalMemory", ct, dimm =>
        {
            dimmSum += ReadUInt64(dimm, "Capacity");
            dimmCount++;
        });

        if (dimmSum > 0)
        {
            fields.Add(new InfoRow("ОЗУ (модули)", $"{FormatBytes(dimmSum)} / {dimmCount} шт."));
        }

        ForEach("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace, DriveType FROM Win32_LogicalDisk", ct, disk =>
        {
            if (ReadUInt64(disk, "DriveType") != 3)
            {
                return;
            }

            var size = ReadUInt64(disk, "Size");
            var free = ReadUInt64(disk, "FreeSpace");
            var usedPercent = size == 0
                ? "—"
                : ((size - free) * 100.0 / size).ToString("0.0", CultureInfo.InvariantCulture) + " %";

            disks.Add(new DiskInfo(
                ReadString(disk, "DeviceID"),
                ReadString(disk, "VolumeName"),
                ReadString(disk, "FileSystem"),
                FormatBytes(size),
                FormatBytes(free),
                FormatBytes(size > free ? size - free : 0),
                usedPercent)
            {
                TotalBytes = size,
                FreeBytes = free
            });
        });

        ForEach("SELECT Name, AdapterRAM FROM Win32_VideoController", ct, gpu =>
        {
            var name = ReadString(gpu, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var ram = ReadUInt64(gpu, "AdapterRAM");
            video.Add(ram > 0 ? $"{name} ({FormatBytes(ram)})" : name);
        });

        // IP-адрес: IPv4/IPv6 включённых адаптеров (пустые и loopback-адреса прочь).
        var addresses = new List<string>();
        ForEach("SELECT IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE", ct, cfg =>
        {
            try
            {
                if (cfg["IPAddress"] is not string[] ips)
                {
                    return;
                }

                addresses.AddRange(ips.Where(ip => !string.IsNullOrWhiteSpace(ip)));
            }
            catch
            {
                // Не массив/пусто — адаптер без адресов пропускаем.
            }
        });

        var ipField = addresses.Distinct().ToList();
        fields.Add(new InfoRow("IP-адрес", ipField.Count > 0 ? string.Join(", ", ipField) : "—"));

        ForEach("SELECT Name, MACAddress, PhysicalAdapter FROM Win32_NetworkAdapter", ct, nic =>
        {
            var name = ReadString(nic, "Name");
            if (string.IsNullOrWhiteSpace(name) || name.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var mac = ReadString(nic, "MACAddress");
            var physical = ReadUInt64(nic, "PhysicalAdapter") == 1;
            if (!physical && string.IsNullOrWhiteSpace(mac))
            {
                return;
            }

            network.Add(string.IsNullOrWhiteSpace(mac) ? name : $"{name} ({mac})");
        });

        if (fields.Count == 0)
        {
            throw new InvalidOperationException("WMI не вернул данные об операционной системе.");
        }

        return new SystemInfo
        {
            Fields = fields,
            Disks = disks,
            VideoAdapters = video,
            NetworkAdapters = network,
            OsCaption = string.IsNullOrWhiteSpace(osCaption) ? null : osCaption,
            OsVersion = string.IsNullOrWhiteSpace(osVersion) ? null : osVersion,
            CpuSummary = string.IsNullOrWhiteSpace(cpuSummary) ? null : cpuSummary,
            RamTotalBytes = ramTotalBytes
        };
    }

    private static void ForEach(string query, CancellationToken ct, Action<ManagementObject> action)
    {
        ct.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher(query);
        using var collection = searcher.Get();
        foreach (var item in collection)
        {
            ct.ThrowIfCancellationRequested();
            using var obj = (ManagementObject)item;
            action(obj);
        }
    }

    private static string ReadString(ManagementBaseObject obj, string name)
    {
        try
        {
            var value = obj[name];
            return value?.ToString()?.Trim() ?? string.Empty;
        }
        catch (ManagementException)
        {
            return string.Empty;
        }
    }

    private static ulong ReadUInt64(ManagementBaseObject obj, string name)
    {
        try
        {
            var value = obj[name];
            return value switch
            {
                null => 0,
                ulong u => u,
                uint u => u,
                int i when i >= 0 => (ulong)i,
                long l when l >= 0 => (ulong)l,
                bool b => b ? 1u : 0u,
                string s when ulong.TryParse(s, out var parsed) => parsed,
                _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadCimDate(ManagementBaseObject obj, string name)
    {
        var dt = ReadCimDateTime(obj, name);
        return dt is null ? "—" : dt.Value.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }

    // Режим прошивки: 1) реестр PEFirmwareType (1 = Legacy, 2 = UEFI) — есть не
    // на всех сборках; 2) зонд GetFirmwareEnvironmentVariableW — на Legacy BIOS
    // всегда ERROR_INVALID_FUNCTION, на UEFI — успех или иной код ошибки.
    private static string ReadFirmwareMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            if (key?.GetValue("PEFirmwareType") is int type)
            {
                return type == 2 ? "UEFI" : "Legacy (BIOS)";
            }
        }
        catch
        {
            // Ключа нет — определяем через API ниже.
        }

        try
        {
            GetFirmwareEnvironmentVariableW(
                string.Empty, "{00000000-0000-0000-0000-000000000000}", IntPtr.Zero, 0);
            return Marshal.GetLastWin32Error() == 1 /* ERROR_INVALID_FUNCTION */
                ? "Legacy (BIOS)"
                : "UEFI";
        }
        catch
        {
            return "—";
        }
    }

    private static DateTime? ReadCimDateTime(ManagementBaseObject obj, string name)
    {
        var raw = ReadString(obj, name);
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

    private static string FormatBytes(ulong bytes)
    {
        if (bytes == 0)
        {
            return "0 Б";
        }

        double value = bytes;
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : "0.0";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string FormatUptime(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays} д. {value.Hours} ч. {value.Minutes} мин.";
        }

        if (value.TotalHours >= 1)
        {
            return $"{value.Hours} ч. {value.Minutes} мин.";
        }

        return $"{value.Minutes} мин.";
    }
}
