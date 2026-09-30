using System.Globalization;
using System.Management;
using SCU.Common;
using SCU.Models.Drivers;

namespace SCU.Infrastructure.Windows.Drivers;

// Инвентарь драйверов ключевого оборудования: Win32_PnPSignedDriver (версия/дата/
// провайдер) + проблемные устройства из Win32_PnPEntity (ConfigManagerErrorCode > 0).
// Паттерн чтения WMI — как в SystemInfoService (ForEach + Read-хелперы, Task.Run).
// Полный список устройств не нужен: показываются значимые классы PnP.
public sealed class DriverInventoryService
{
    // Классы оборудования, для которых обновление драйвера имеет смысл показывать
    // пользователю (system-устройств сотни, список становился бы шумом).
    internal static readonly IReadOnlySet<string> SignificantClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DISPLAY",   // видеокарты
        "NET",       // сетевые адаптеры
        "MEDIA",     // аудио
        "SCSIADAPTER", // контроллеры хранения
        "HDC",       // IDE/ATA-контроллеры
        "SYSTEM",    // чипсет/мосты
        "USB",       // USB-контроллеры
    };

    public async Task<Result<DriverInventory>> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var inventory = await Task.Run(() => Read(ct), ct).ConfigureAwait(false);
            return Result<DriverInventory>.Success(inventory);
        }
        catch (OperationCanceledException)
        {
            return Result<DriverInventory>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result<DriverInventory>.Failure(exception.Message);
        }
    }

    private static DriverInventory Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var problems = new Dictionary<string, DriverProblemInfo>(StringComparer.OrdinalIgnoreCase);
        ForEach(
            "SELECT Name, DeviceID, Manufacturer, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode > 0",
            ct,
            entity =>
            {
                var deviceId = ReadString(entity, "DeviceID");
                if (deviceId.Length == 0)
                {
                    return;
                }

                problems[deviceId] = new DriverProblemInfo(
                    ReadString(entity, "Name"),
                    (uint)(Convert.ToInt64(entity.Properties["ConfigManagerErrorCode"].Value ?? 0, CultureInfo.InvariantCulture)),
                    ReadString(entity, "Manufacturer"),
                    deviceId);
            });

        var devices = new List<DriverDeviceInfo>();
        ForEach(
            "SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate, DeviceClass, DeviceID "
            + "FROM Win32_PnPSignedDriver WHERE DeviceClass IS NOT NULL",
            ct,
            driver =>
            {
                var deviceClass = ReadString(driver, "DeviceClass");
                if (!SignificantClasses.Contains(deviceClass))
                {
                    return;
                }

                var name = ReadString(driver, "DeviceName");
                if (name.Length == 0)
                {
                    return;
                }

                var deviceId = ReadString(driver, "DeviceID");
                devices.Add(new DriverDeviceInfo(
                    name,
                    ReadString(driver, "DriverProviderName"),
                    ReadString(driver, "DriverVersion"),
                    ParseCimDate(ReadString(driver, "DriverDate")),
                    deviceClass.ToUpperInvariant(),
                    HasProblem: deviceId.Length > 0 && problems.ContainsKey(deviceId),
                    ProblemCode: deviceId.Length > 0 && problems.TryGetValue(deviceId, out var problem) ? problem.ProblemCode : 0));
            });

        devices.Sort((left, right) =>
        {
            var byProblem = right.HasProblem.CompareTo(left.HasProblem);
            return byProblem != 0 ? byProblem : string.CompareOrdinal(left.DeviceClass, right.DeviceClass);
        });

        ct.ThrowIfCancellationRequested();
        return new DriverInventory(devices, [.. problems.Values.OrderBy(problem => problem.Name)]);
    }

    // DMTF-дата Win32_PnPSignedDriver ("20240214000000.000000-000") → дата.
    // Читаем первые 8 символов (yyyyMMdd): полный ManagementDateTimeConverter
    // избыточен, а частичные/битые значения не должны ронять инвентарь.
    internal static DateTime? ParseCimDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 8)
        {
            return null;
        }

        return DateTime.TryParseExact(
            raw[..8],
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }

    private static void ForEach(string query, CancellationToken ct, Action<ManagementBaseObject> action)
    {
        using var searcher = new ManagementObjectSearcher(query);
        foreach (var item in searcher.Get())
        {
            using (item)
            {
                ct.ThrowIfCancellationRequested();
                action(item);
            }
        }
    }

    private static string ReadString(ManagementBaseObject obj, string name)
    {
        try
        {
            return obj.Properties[name]?.Value?.ToString() ?? string.Empty;
        }
        catch (ManagementException)
        {
            return string.Empty;
        }
    }
}
