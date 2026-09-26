using Microsoft.Win32;
using System.Management;
using System.ServiceProcess;
using SCU.Common;
using SCU.Models;

namespace SCU.Services;

public sealed class ServiceManager
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);
    public static IReadOnlyList<string> DefaultServiceNames { get; } =
    [
        "SysMain",
        "DiagTrack",
        "dmwappushservice",
        "WSearch",
        "Fax",
        "XblAuthManager",
        "XblGameSave",
        "XboxGipSvc",
        "XboxNetApiSvc",
        "RemoteRegistry",
        "RemoteAccess",
        "WbioSrvc",
        "TabletInputService",
        "MapsBroker",
        "RetailDemo",
        "wisvc",
        "WerSvc",
        "PcaSvc",
        "PrintNotify",
        "Spooler"
    ];

    public static string DefaultServicesCsv => string.Join(",", DefaultServiceNames);

    public Task<Result<IReadOnlyList<WindowsServiceInfo>>> GetDefaultServicesAsync(CancellationToken ct = default)
        => GetServicesAsync(DefaultServiceNames, ct);

    public async Task<Result<WindowsServiceInfo>> GetServiceAsync(string serviceName, CancellationToken ct = default)
    {
        var result = await GetServicesAsync([serviceName], ct).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null || result.Value.Count == 0)
        {
            return Result<WindowsServiceInfo>.Failure(result.Message, result.Code);
        }

        return Result<WindowsServiceInfo>.Success(result.Value[0]);
    }

    public async Task<Result> SetDisabledAsync(string serviceName, bool disable, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return Result.Failure("Не задано имя службы.");
        }

        try
        {
            return await Task.Run(() => ApplyToggle(serviceName, disable, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure(exception.Message);
        }
    }

    // Остановка службы без изменения типа запуска — для очистки кэша обновлений.
    // Отсутствующая или уже остановленная служба ошибкой не считается.
    public async Task<Result> StopAsync(string serviceName, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => StopService(serviceName, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure($"Служба {serviceName}: {exception.Message}");
        }
    }

    // Возврат службы в автозапуск — для отката DiagTrack/DoSvc (sc config ... start= auto).
    // ChangeStartMode(Automatic) для ряда служб (DoSvc и др.) возвращает ошибку —
    // тогда Start=2 + DelayedAutostart=1 пишутся напрямую в реестр с проверкой.
    public async Task<Result> SetAutomaticAsync(string serviceName, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    SetStartMode(serviceName, ServiceStartMode.Automatic);
                }
                catch (Exception)
                {
                    // Сначала обычный Automatic (Start=2) без DelayedAutostart.
                    // ForceAutomaticDelayed только если простой fallback не сработал.
                    if (ForceStartValue(serviceName, 2))
                    {
                        return Result.Success($"{serviceName}: автозапуск восстановлен через реестр (без отложенного старта).");
                    }

                    if (!ForceAutomaticDelayed(serviceName))
                    {
                        return Result.Failure($"Служба {serviceName}: не удалось задать автозапуск.");
                    }

                    return Result.Success(
                        $"{serviceName}: автозапуск восстановлен через реестр как Automatic (Delayed). "
                        + "Исходный тип без Delayed восстановить не удалось.");
                }
                using var controller = OpenController(serviceName);
                if (controller is not null && controller.Status != ServiceControllerStatus.Running)
                {
                    try
                    {
                        controller.Start();
                        controller.WaitForStatus(ServiceControllerStatus.Running, ControlTimeout);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or System.TimeoutException)
                    {
                        return Result.Success($"{serviceName}: тип запуска восстановлен, но служба не запущена ({exception.Message}).");
                    }
                }

                return Result.Success($"{serviceName}: автозапуск восстановлен.");
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure($"Служба {serviceName}: {exception.Message}");
        }
    }

    // Включение отключённой службы: тип запуска из резерва (Automatic, если было 2, иначе Manual),
    // сам запуск службы не выполняется. Раньше включение шло через ServicesRestore, который
    // восстанавливал исходно выключенное состояние — «включение» оставляло службу выключенной.
    public async Task<Result> SetEnabledAsync(string serviceName, string? backupFilePath, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var startFromBackup = ReadStartFromBackup(backupFilePath, serviceName);
                var targetMode = startFromBackup == 2 ? ServiceStartMode.Automatic : ServiceStartMode.Manual;

                try
                {
                    SetStartMode(serviceName, targetMode);
                }
                catch (Exception)
                {
                    // Fallback пишет только целевой режим: ручной запуск не должен
                    // превращаться в отложенный автозапуск при неудачной смене режима.
                    var forced = targetMode == ServiceStartMode.Automatic
                        ? ForceAutomaticDelayed(serviceName)
                        : ForceStartValue(serviceName, 3);
                    if (!forced)
                    {
                        return Result.Failure($"Служба {serviceName}: не удалось задать тип запуска.");
                    }
                }

                if (targetMode == ServiceStartMode.Automatic && ReadDelayedAutostart(serviceName))
                {
                    WriteDelayedAutostart(serviceName, true);
                }

                var actualStart = ReadStartValue(serviceName);
                var expected = targetMode == ServiceStartMode.Automatic ? 2 : 3;
                if (actualStart != expected)
                {
                    return Result.Failure($"Служба {serviceName}: тип запуска не применился (Start={actualStart}, ожидалось {expected}).");
                }

                var caption = targetMode == ServiceStartMode.Automatic ? "автозапуск" : "ручной запуск";
                return Result.Success($"{serviceName}: включена ({caption}).");
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure($"Служба {serviceName}: {exception.Message}");
        }
    }

    // Свежий резерв служб (services_*.txt) — подсказка исходного типа запуска для «включить».
    private static string? FindLatestServicesBackupPath()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCU", "backup", "services");
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(directory, "services_*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    // Start из строки резерва "name|start|state|delayed"; резерв повреждён/нет записи — Manual.
    internal static int? ReadStartFromBackup(string? backupFilePath, string serviceName)    {
        try
        {
            if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(backupFilePath))
            {
                var parts = line.Split('|');
                if (parts.Length >= 2 && string.Equals(parts[0].Trim(), serviceName, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[1].Trim(), out var start))
                {
                    return start;
                }
            }
        }
        catch
        {
            // Резерв читается только как подсказка; ошибка чтения не ломает включение.
        }

        return null;
    }

    // Start=2 + DelayedAutostart=1 напрямую в реестре (для служб, запрещающих ChangeStartMode).
    private static bool ForceAutomaticDelayed(string serviceName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue("Start", 2, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("DelayedAutostart", 1, Microsoft.Win32.RegistryValueKind.DWord);
            return ReadStartValue(serviceName) == 2;
        }
        catch
        {
            return false;
        }
    }

    // Запись Start напрямую в реестр (fallback для DoSvc и других служб, запрещающих ChangeStartMode).
    private static bool ForceStartValue(string serviceName, int startValue)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue("Start", startValue, Microsoft.Win32.RegistryValueKind.DWord);
            return ReadStartValue(serviceName) == startValue;
        }
        catch
        {
            return false;
        }
    }

    private static Result StopService(string serviceName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var controller = OpenControllerChecked(serviceName, out var openStatus);
        if (controller is null)
        {
            // Отсутствующая служба для Stop не ошибка (идемпотентность),
            // отказ в доступе — честная неудача с подсказкой.
            return openStatus switch
            {
                ServiceOpenStatus.AccessDenied => Result.Failure(
                    $"Служба {serviceName}: нет доступа — запустите SCU от имени администратора."),
                ServiceOpenStatus.NotFound => Result.Success($"Служба {serviceName} не найдена — пропущена."),
                _ => Result.Failure($"Служба {serviceName}: не удалось получить доступ к службе.")
            };
        }

        controller.Refresh();
        if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            return Result.Success($"{serviceName}: уже остановлена.");
        }

        try
        {
            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, ControlTimeout);
        }
        catch (System.TimeoutException)
        {
            controller.Refresh();
            if (controller.Status == ServiceControllerStatus.Stopped)
                return Result.Success($"{serviceName}: остановлена.");
            return Result.Failure($"{serviceName}: не остановилась за 30 секунд — зависимые службы держат её. Попробуйте позже.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            controller.Refresh();
            if (controller.Status == ServiceControllerStatus.Stopped)
                return Result.Success($"{serviceName}: остановлена.");
            // Не маскируем неудачу под Success.
            return Result.Failure($"{serviceName}: не удалось остановить ({exception.Message}).");
        }

        controller.Refresh();
        if (controller.Status != ServiceControllerStatus.Stopped)
            return Result.Failure($"{serviceName}: после Stop статус = {controller.Status}, ожидался Stopped.");

        return Result.Success($"{serviceName}: остановлена.");
    }

    public async Task<Result<IReadOnlyList<WindowsServiceInfo>>> GetServicesAsync(
        IEnumerable<string> serviceNames,
        CancellationToken ct = default)
    {
        var names = serviceNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            var result = await Task.Run(
                () => QueryServices(names, ct),
                ct).ConfigureAwait(false);
            return Result<IReadOnlyList<WindowsServiceInfo>>.Success(result);
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyList<WindowsServiceInfo>>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<WindowsServiceInfo>>.Failure(exception.Message);
        }
    }

    private static IReadOnlyList<WindowsServiceInfo> QueryServices(
        IReadOnlyList<string> serviceNames,
        CancellationToken ct)
    {
        var result = new List<WindowsServiceInfo>(serviceNames.Count);

        foreach (var name in serviceNames)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(QuerySingleService(name));
        }

        return result;
    }

    /// <summary>
    /// Читает фактическое состояние одной службы.
    /// AccessDenied и NotFound различаются; runtime status не сводится к Stopped.
    /// </summary>
    private static WindowsServiceInfo QuerySingleService(string name)
    {
        try
        {
            using var controller = new ServiceController(name);
            // Обращение к свойствам может бросить Win32Exception при Access Denied.
            var displayName = controller.DisplayName;
            var startMode = controller.StartType;
            var status = controller.Status;
            var delayed = ReadDelayedAutostart(name);

            var startupType = MapStartupType(startMode, delayed);
            var runtime = MapRuntimeStatus(status);
            var isStartAllowed = startMode != ServiceStartMode.Disabled;

            return new WindowsServiceInfo(
                name,
                displayName,
                startupType,
                runtime,
                delayed,
                isStartAllowed,
                ServiceQueryStatus.Ok);
        }
        catch (InvalidOperationException)
        {
            // Служба действительно отсутствует в SCM.
            return new WindowsServiceInfo(
                name, name,
                ServiceStartupType.Unknown,
                ServiceRuntimeStatus.Unknown,
                false,
                false,
                ServiceQueryStatus.NotFound);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            // ERROR_ACCESS_DENIED (HRESULT 0x80070005 в NativeErrorCode не попадает) — служба есть, но читать нельзя.
            return new WindowsServiceInfo(
                name, name,
                ServiceStartupType.Unknown,
                ServiceRuntimeStatus.Unknown,
                false,
                false,
                ServiceQueryStatus.AccessDenied);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new WindowsServiceInfo(
                name, name,
                ServiceStartupType.Unknown,
                ServiceRuntimeStatus.Unknown,
                false,
                false,
                ServiceQueryStatus.Error);
        }
        catch (Exception)
        {
            return new WindowsServiceInfo(
                name, name,
                ServiceStartupType.Unknown,
                ServiceRuntimeStatus.Unknown,
                false,
                false,
                ServiceQueryStatus.Unknown);
        }
    }

    private static ServiceStartupType MapStartupType(ServiceStartMode mode, bool delayed) =>
        mode switch
        {
            ServiceStartMode.Boot => ServiceStartupType.Boot,
            ServiceStartMode.System => ServiceStartupType.System,
            ServiceStartMode.Automatic => delayed ? ServiceStartupType.AutomaticDelayed : ServiceStartupType.Automatic,
            ServiceStartMode.Manual => ServiceStartupType.Manual,
            ServiceStartMode.Disabled => ServiceStartupType.Disabled,
            _ => ServiceStartupType.Unknown
        };

    private static ServiceRuntimeStatus MapRuntimeStatus(ServiceControllerStatus status) =>
        status switch
        {
            ServiceControllerStatus.Running => ServiceRuntimeStatus.Running,
            ServiceControllerStatus.Stopped => ServiceRuntimeStatus.Stopped,
            ServiceControllerStatus.StartPending => ServiceRuntimeStatus.StartPending,
            ServiceControllerStatus.StopPending => ServiceRuntimeStatus.StopPending,
            ServiceControllerStatus.ContinuePending => ServiceRuntimeStatus.ContinuePending,
            ServiceControllerStatus.PausePending => ServiceRuntimeStatus.PausePending,
            ServiceControllerStatus.Paused => ServiceRuntimeStatus.Paused,
            _ => ServiceRuntimeStatus.Unknown
        };

    private static Result ApplyToggle(string serviceName, bool disable, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var controller = OpenControllerChecked(serviceName, out var openStatus);
        if (controller is null)
        {
            return openStatus switch
            {
                ServiceOpenStatus.NotFound => Result.Failure($"Служба {serviceName} отсутствует."),
                ServiceOpenStatus.AccessDenied => Result.Failure(
                    $"Служба {serviceName}: нет доступа — запустите SCU от имени администратора."),
                _ => Result.Failure($"Служба {serviceName}: не удалось получить доступ к службе.")
            };
        }

        var previousStart = ReadStartValue(serviceName);
        var previousDelayed = ReadDelayedAutostart(serviceName);
        var previousStatus = controller.Status;

        ServiceStartMode? targetMode = null;

        if (disable)
        {
            if (controller.Status != ServiceControllerStatus.Stopped
                && controller.Status != ServiceControllerStatus.StopPending)
            {
                try
                {
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, ControlTimeout);
                }
                catch (InvalidOperationException exception)
                {
                    return Result.Failure($"{serviceName}: не удалось остановить ({exception.Message}). Возможно, службу держат зависимые службы или она защищена системой.");
                }
                catch (Exception exception) when (exception.GetType().Name == "TimeoutException")
                {
                    return Result.Failure($"{serviceName}: не остановилась за 30 секунд — зависимые службы держат её. Попробуйте позже.");
                }
            }

            // DoSvc и другие службы запрещают ChangeStartMode — исключение не фатально:
            // Start допишется напрямую в реестр проверкой ниже.
            try
            {
                SetStartMode(serviceName, ServiceStartMode.Disabled);
            }
            catch
            {
            }
        }
        else
        {
            // Тумблер «включить»: текущий Start=4 не говорит, каким служба была до отключения.
            // Берём исходный тип из последнего резерва служб; без подсказки — Manual.
            // Раньше всегда форсировался Manual — бывшая Automatic служба навсегда понижалась.
            var startFromBackup = ReadStartFromBackup(FindLatestServicesBackupPath(), serviceName);
            targetMode = previousStart == 2 || startFromBackup == 2
                ? ServiceStartMode.Automatic
                : ServiceStartMode.Manual;
            try
            {
                SetStartMode(serviceName, targetMode.Value);
            }
            catch
            {
            }

            if (previousDelayed && targetMode == ServiceStartMode.Automatic)
            {
                WriteDelayedAutostart(serviceName, true);
            }
        }

        // Ряд служб (DoSvc и др.) запрещает ChangeStartMode — тогда Start пишется напрямую
        // в реестр и проверяется чтением.
        var expectedStart = disable ? 4 : targetMode == ServiceStartMode.Automatic ? 2 : 3;
        if (ReadStartValue(serviceName) != expectedStart && !ForceStartValue(serviceName, expectedStart))
        {
            return Result.Failure(
                $"Служба {serviceName}: тип запуска не применился (Start={ReadStartValue(serviceName)}, ожидалось {expectedStart}).");
        }

        controller.Refresh();
        var actualStart = ReadStartValue(serviceName);
        if (actualStart != expectedStart)
        {
            return Result.Failure($"Служба {serviceName}: тип запуска не применился (Start={actualStart}, ожидалось {expectedStart}).");
        }

        if (disable && controller.Status != ServiceControllerStatus.Stopped)
        {
            return Result.Failure($"Служба {serviceName}: не остановилась.");
        }

        var verb = disable ? "отключена" : "включена";
        return Result.Success($"Служба {serviceName} {verb}. Было: Start={previousStart}, State={previousStatus}.");
    }

    private enum ServiceOpenStatus
    {
        Ok,
        NotFound,
        AccessDenied,
        Error
    }

    private static ServiceController? OpenController(string serviceName)
        => OpenControllerChecked(serviceName, out _);

    // NotFound (InvalidOperationException) и AccessDenied (Win32Exception 5) различаются:
    // отказ в доступе не смеет маскироваться под «службы нет».
    private static ServiceController? OpenControllerChecked(string serviceName, out ServiceOpenStatus status)
    {
        ServiceController? controller = null;
        try
        {
            controller = new ServiceController(serviceName);
            _ = controller.Status;
            status = ServiceOpenStatus.Ok;
            return controller;
        }
        catch (InvalidOperationException)
        {
            controller?.Dispose();
            status = ServiceOpenStatus.NotFound;
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            controller?.Dispose();
            status = ServiceOpenStatus.AccessDenied;
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            controller?.Dispose();
            status = ServiceOpenStatus.Error;
            return null;
        }
    }

    private static int ReadStartValue(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
        var value = key?.GetValue("Start");
        return value switch
        {
            int intValue => intValue,
            uint uintValue => (int)uintValue,
            _ => -1
        };
    }

    private static bool ReadDelayedAutostart(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
        var value = key?.GetValue("DelayedAutostart");
        return value switch
        {
            int intValue => intValue == 1,
            uint uintValue => uintValue == 1,
            long longValue => longValue == 1,
            _ => false
        };
    }

    private static void WriteDelayedAutostart(string serviceName, bool delayed)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
        key?.SetValue("DelayedAutostart", delayed ? 1 : 0, RegistryValueKind.DWord);
    }

    private static void SetStartMode(string serviceName, ServiceStartMode mode)
    {
        var wmiMode = mode switch
        {
            ServiceStartMode.Boot => "Boot",
            ServiceStartMode.System => "System",
            ServiceStartMode.Automatic => "Automatic",
            ServiceStartMode.Manual => "Manual",
            ServiceStartMode.Disabled => "Disabled",
            _ => "Manual"
        };

        using var service = new ManagementObject($"Win32_Service.Name='{EscapeWmi(serviceName)}'");
        try
        {
            service.Get();
        }
        catch (ManagementException exception)
        {
            throw new InvalidOperationException($"WMI не нашёл службу {serviceName}.", exception);
        }

        using var inParams = service.GetMethodParameters("ChangeStartMode");
        inParams["StartMode"] = wmiMode;
        using var outParams = service.InvokeMethod("ChangeStartMode", inParams, null);
        var returnValue = Convert.ToInt32(outParams?["ReturnValue"] ?? -1);
        if (returnValue != 0)
        {
            throw new InvalidOperationException($"ChangeStartMode={returnValue} для {serviceName}.");
        }
    }

    private static string EscapeWmi(string value) => value.Replace("'", "\\'", StringComparison.Ordinal);
}
