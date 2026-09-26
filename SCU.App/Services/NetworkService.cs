using System.Globalization;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;

namespace SCU.Services;

// Раздел 9 «Utilities.bat» — сеть: TCP Global (Auto-Tuning, ECN), MTU, QoS override, NetBIOS и профиль NIC.
// Основные изменения используют резерв → изменение → проверка; профиль NIC делает последовательные изменения, один перезапуск адаптера и затем проверку.
public sealed class NetworkService
{
    private const string PschedSubKey = @"SOFTWARE\Policies\Microsoft\Windows\Psched";
    private const string PschedValueName = "NonBestEffortLimit";

    private static readonly string BackupDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "network");

    private readonly Logger _logger;
    private readonly LongProcessRunner _runner;

    public NetworkService(Logger logger, LongProcessRunner runner)
    {
        _logger = logger;
        _runner = runner;
    }

    // Текущие Auto-Tuning / ECN из вывода netsh (regex не зависит от раскладки значений).
    public sealed record TcpGlobalState(string? AutoTuning, string? Ecn);

    public async Task<Result<TcpGlobalState>> GetTcpGlobalAsync(CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync("netsh", ["int", "tcp", "show", "global"], null, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result<TcpGlobalState>.Failure(result.Message, result.Code);
        }

        var autoTuning = (string?)null;
        var ecn = (string?)null;
        foreach (var line in (result.Value ?? string.Empty).Split('\n'))
        {
            var autoMatch = Regex.Match(
                line,
                @"(?:Auto.?Tuning|автонастр\w*|автоматическ\w*).{0,100}:\s*(disabled|highlyrestricted|restricted|normal|experimental)\s*$",
                RegexOptions.IgnoreCase);
            if (autoMatch.Success)
            {
                autoTuning = autoMatch.Groups[1].Value.ToLowerInvariant();
            }

            var ecnMatch = Regex.Match(line, @"ECN[^:]*:\s*(enabled|disabled|default)\s*$", RegexOptions.IgnoreCase);
            if (ecnMatch.Success)
            {
                ecn = ecnMatch.Groups[1].Value.ToLowerInvariant();
            }
        }

        if (autoTuning is null || ecn is null)
        {
            return Result<TcpGlobalState>.Failure("Не удалось разобрать TCP Global параметры из netsh.");
        }

        return Result<TcpGlobalState>.Success(new TcpGlobalState(autoTuning, ecn));
    }

    // Резерв TCP Global + применение значения. Повторный резерв не перезаписывает исходное (как в BAT).
    public async Task<Result> SetTcpGlobalAsync(string setting, string value, CancellationToken ct = default)
    {
        var backup = await BackupTcpGlobalAsync(ct).ConfigureAwait(false);
        if (!backup.IsSuccess)
        {
            return Result.Failure("Не удалось сохранить исходные TCP Global параметры — изменение отменено.", backup.Code);
        }

        var set = await _runner
            .RunAsync("netsh", ["int", "tcp", "set", "global", $"{setting}={value}"], null, ct)
            .ConfigureAwait(false);
        _logger.Info($"NET | {setting}={value} | rc={set.Code}");
        if (!set.IsSuccess)
        {
            return Result.Failure(set.Message, set.Code);
        }

        var verify = await GetTcpGlobalAsync(ct).ConfigureAwait(false);
        if (!verify.IsSuccess)
        {
            return Result.Failure("Изменение применено, но не подтверждено чтением: " + verify.Message);
        }

        var actual = setting.StartsWith("autotuning", StringComparison.OrdinalIgnoreCase)
            ? verify.Value?.AutoTuning
            : verify.Value?.Ecn;
        if (!string.Equals(actual, value, StringComparison.OrdinalIgnoreCase)
            && !(setting.StartsWith("autotuning", StringComparison.OrdinalIgnoreCase) && value == "default" && actual == "normal"))
        {
            return Result.Failure($"Параметр не применился: фактическое значение {actual}.");
        }

        return Result.Success($"{setting} = {value}.");
    }

    public async Task<Result> RestoreTcpGlobalAsync(CancellationToken ct = default)
    {
        var backupFile = TcpGlobalBackupFile;
        if (!File.Exists(backupFile))
        {
            return Result.Success("Резерв TCP Global отсутствует — восстанавливать нечего.");
        }

        var lines = File.ReadAllLines(backupFile);
        string? autoTuning = null;
        string? ecn = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("autotuning=", StringComparison.OrdinalIgnoreCase))
            {
                autoTuning = line["autotuning=".Length..].Trim();
            }

            if (line.StartsWith("ecn=", StringComparison.OrdinalIgnoreCase))
            {
                ecn = line["ecn=".Length..].Trim();
            }
        }

        if (autoTuning is null || ecn is null)
        {
            return Result.Failure("Резерв TCP Global повреждён.");
        }

        var restoreAuto = await _runner
            .RunAsync("netsh", ["int", "tcp", "set", "global", $"autotuninglevel={autoTuning}"], null, ct)
            .ConfigureAwait(false);
        var restoreEcn = await _runner
            .RunAsync("netsh", ["int", "tcp", "set", "global", $"ecncapability={ecn}"], null, ct)
            .ConfigureAwait(false);
        _logger.Info($"NET | restore TCP Global | auto rc={restoreAuto.Code} | ecn rc={restoreEcn.Code}");
        if (!restoreAuto.IsSuccess || !restoreEcn.IsSuccess)
        {
            return Result.Failure("TCP Global не восстановлены; резерв сохранён.");
        }

        var verify = await GetTcpGlobalAsync(ct).ConfigureAwait(false);
        if (!verify.IsSuccess
            || !string.Equals(verify.Value?.AutoTuning, autoTuning, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(verify.Value?.Ecn, ecn, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("TCP Global не подтверждены после восстановления; резерв сохранён.");
        }

        // Удаление резерва — необязательный хвост: не роняет успешную операцию.
        try
        {
            File.Delete(backupFile);
        }
        catch (Exception exception)
        {
            _logger.Warn("NET | TCP Global backup cleanup failed: " + exception.Message);
        }

        return Result.Success($"TCP Global восстановлены (Auto-Tuning={autoTuning}, ECN={ecn}).");
    }

    public async Task<Result> SetMtuAsync(string interfaceAlias, int mtu, CancellationToken ct = default)
    {
        if (mtu is < 576 or > 1500)
        {
            return Result.Failure("MTU должен быть в диапазоне 576..1500.");
        }

        var backup = await BackupMtuAsync(interfaceAlias, ct).ConfigureAwait(false);
        if (!backup.IsSuccess)
        {
            return Result.Failure("Не удалось сохранить исходный MTU — изменение отменено.", backup.Code);
        }

        var set = await _runner
            .RunAsync(
                "netsh",
                ["interface", "ipv4", "set", "subinterface", interfaceAlias, $"mtu={mtu}", "store=persistent"],
                null,
                ct).ConfigureAwait(false);
        _logger.Info($"NET | MTU {interfaceAlias}={mtu} | rc={set.Code}");
        if (!set.IsSuccess)
        {
            return Result.Failure(set.Message, set.Code);
        }

        var verify = await GetCurrentMtuAsync(interfaceAlias, ct).ConfigureAwait(false);
        if (!verify.IsSuccess)
        {
            return Result.Failure("MTU изменён, но не подтверждён чтением: " + verify.Message, verify.Code);
        }

        if (verify.Value != mtu)
        {
            return Result.Failure($"MTU для {interfaceAlias} не применился: фактически {verify.Value}, ожидалось {mtu}.");
        }

        return Result.Success($"MTU {mtu} установлен для {interfaceAlias} и подтверждён чтением.");
    }

    public async Task<Result> RestoreMtuAsync(CancellationToken ct = default)
    {
        var backupFile = MtuBackupFile;
        if (!File.Exists(backupFile))
        {
            return Result.Success("Резерв MTU отсутствует — восстанавливать нечего.");
        }

        var failures = 0;
        foreach (var line in File.ReadAllLines(backupFile))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mtu))
            {
                failures++;
                continue;
            }

            var restore = await _runner
                .RunAsync(
                    "netsh",
                    ["interface", "ipv4", "set", "subinterface", parts[0], $"mtu={mtu}", "store=persistent"],
                    null,
                    ct).ConfigureAwait(false);
            if (!restore.IsSuccess)
            {
                failures++;
            }
        }

        if (failures > 0)
        {
            return Result.Failure("MTU восстановлен не полностью; резерв сохранён.");
        }

        // Удаление резерва — необязательный хвост: не роняет успешную операцию.
        try
        {
            File.Delete(backupFile);
        }
        catch (Exception exception)
        {
            _logger.Warn("NET | MTU backup cleanup failed: " + exception.Message);
        }

        return Result.Success("MTU восстановлены из резерва.");
    }

    // Только чтение: null — значение (или ключ Psched) отсутствует.
    public int? GetQosOverride()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PschedSubKey);
        var value = key?.GetValue(PschedValueName);
        return value switch
        {
            int intValue => intValue,
            _ => null
        };
    }

    public Result SetQosOverride(int? value)
    {
        var backupFile = QosBackupFile;
        var current = GetQosOverride();
        try
        {
            // Повторный резерв не перезаписывает исходное значение (как :NetQoSSave в BAT).
            if (!File.Exists(backupFile))
            {
                Directory.CreateDirectory(BackupDirectory);
                File.WriteAllText(backupFile, current is null ? "absent" : "present=" + current.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось сохранить исходный QoS: " + exception.Message);
        }

        try
        {
            if (value is null)
            {
                using var key = Registry.LocalMachine.OpenSubKey(PschedSubKey, writable: true);
                if (key is not null)
                {
                    key.DeleteValue(PschedValueName, throwOnMissingValue: false);
                }
            }
            else
            {
                using var key = Registry.LocalMachine.CreateSubKey(PschedSubKey, writable: true);
                key.SetValue(PschedValueName, value.Value, RegistryValueKind.DWord);
            }
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось изменить QoS: " + exception.Message);
        }

        var verify = GetQosOverride();
        if (verify != value)
        {
            return Result.Failure("QoS override не применился.");
        }

        _logger.Info($"NET | QoS override = {(value is null ? "(удалён)" : value)}");
        return Result.Success(value is null ? "QoS override удалён." : $"QoS override = {value}%.");
    }

    public Result RestoreQosOverride()
    {
        var backupFile = QosBackupFile;
        if (!File.Exists(backupFile))
        {
            return Result.Success("Резерв QoS отсутствует — восстанавливать нечего.");
        }

        var content = File.ReadAllText(backupFile).Trim();
        Result result;
        if (content == "absent")
        {
            result = SetQosOverride(null);
        }
        else if (content.StartsWith("present=", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(
                     content["present=".Length..],
                     NumberStyles.Integer,
                     CultureInfo.InvariantCulture,
                     out var savedValue)
                 && savedValue is >= 0 and <= 100)
        {
            result = SetQosOverride(savedValue);
        }
        else
        {
            return Result.Failure("Резерв QoS повреждён: ожидается absent или present=<0..100>.");
        }
        if (result.IsSuccess)
        {
            // Удаление резерва — необязательный хвост: не роняет успешную операцию.
            try
            {
                File.Delete(backupFile);
            }
            catch (Exception exception)
            {
                _logger.Warn("NET | QoS backup cleanup failed: " + exception.Message);
            }
        }

        return result;
    }

    // NetBIOS over TCP/IP: резерв по индексам интерфейсов через WMI, изменение SetTcpipNetbios.
    public Task<Result> SetNetBiosAsync(int mode, CancellationToken ct = default)
    {
        // WMI-вызовы блокирующие — уводим в пул, чтобы UI не залипал. Токен проверяется
        // первым делом внутри делегата: pre-cancelled токен даёт честный OCE, а не
        // TaskCanceledException мимо контракта (как в TaskRunner).
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return SetNetBiosCore(mode, ct);
        }, CancellationToken.None);
    }

    private Result SetNetBiosCore(int mode, CancellationToken ct)
    {
        if (mode is not (0 or 1 or 2))
        {
            return Result.Failure("Режим NetBIOS должен быть 0, 1 или 2.");
        }

        var backupFile = NetBiosBackupFile;
        try
        {
            if (!File.Exists(backupFile))
            {
                var snapshots = new List<string>();
                foreach (var adapter in QueryNetBiosAdapters())
                {
                    snapshots.Add($"{adapter.Index}\t{adapter.Options}");
                }

                Directory.CreateDirectory(BackupDirectory);
                File.WriteAllLines(backupFile, snapshots);
                _logger.Info($"NET | NetBIOS backup | {snapshots.Count} интерфейсов");
            }

            var failures = 0;
            var count = 0;
            foreach (var adapter in QueryNetBiosAdapters())
            {
                count++;
                ct.ThrowIfCancellationRequested();
                using var adapterObject = new ManagementObject(
                    $@"\\.\root\cimv2:Win32_NetworkAdapterConfiguration.Index={adapter.Index}");
                using var inParams = adapterObject.GetMethodParameters("SetTcpipNetbios");
                inParams["TcpipNetbiosOptions"] = mode;
                using var outParams = adapterObject.InvokeMethod("SetTcpipNetbios", inParams, null);
                var returnValue = Convert.ToInt32(outParams?["ReturnValue"] ?? -1);
                if (returnValue != 0)
                {
                    failures++;
                    _logger.Warn($"NET | NetBIOS {adapter.Index} rc={returnValue}");
                }
            }

            if (count == 0 || failures > 0)
            {
                return Result.Failure("NetBIOS: один или несколько интерфейсов не изменены.");
            }

            var verify = QueryNetBiosAdapters().Count(a => a.Options == mode);
            if (verify != count)
            {
                return Result.Failure("NetBIOS: проверка после изменения не прошла.");
            }

            _logger.Info($"NET | NetBIOS mode={mode} applied | {count} интерфейсов");
            return Result.Success($"NetBIOS mode {mode} применён ко всем активным интерфейсам.");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("NetBIOS: " + exception.Message);
        }
    }

    public Task<Result> RestoreNetBiosAsync(CancellationToken ct = default)
    {
        // Токен проверяется внутри делегата (см. SetNetBiosAsync), а не вторым аргументом.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return RestoreNetBiosCore(ct);
        }, CancellationToken.None);
    }

    private Result RestoreNetBiosCore(CancellationToken ct)
    {
        var backupFile = NetBiosBackupFile;
        if (!File.Exists(backupFile))
        {
            return Result.Success("Резерв NetBIOS отсутствует — восстанавливать нечего.");
        }

        try
        {
            var adapters = QueryNetBiosAdapters().ToDictionary(a => a.Index);
            var failures = 0;
            var restored = 0;
            foreach (var line in File.ReadAllLines(backupFile))
            {
                ct.ThrowIfCancellationRequested();
                var parts = line.Split('\t');
                if (parts.Length != 2
                    || !uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var savedMode)
                    || savedMode is not (0 or 1 or 2))
                {
                    failures++;
                    continue;
                }

                if (!adapters.TryGetValue(index, out var adapter))
                {
                    continue;
                }

                using var adapterObject = new ManagementObject(
                    $@"\\.\root\cimv2:Win32_NetworkAdapterConfiguration.Index={index}");
                using var inParams = adapterObject.GetMethodParameters("SetTcpipNetbios");
                // Восстанавливаем сохранённый режим из резерва, а не текущее значение адаптера.
                inParams["TcpipNetbiosOptions"] = savedMode;
                using var outParams = adapterObject.InvokeMethod("SetTcpipNetbios", inParams, null);
                if (Convert.ToInt32(outParams?["ReturnValue"] ?? -1) != 0)
                {
                    failures++;
                }
                else
                {
                    restored++;
                    _logger.Info($"NET | NetBIOS restore | index={index} mode={savedMode} (было {adapter.Options})");
                }
            }

            if (failures > 0)
            {
                return Result.Failure("NetBIOS не восстановлен; резерв сохранён.");
            }

            if (restored == 0)
            {
                return Result.Success("Резерв NetBIOS пуст — восстанавливать нечего (адаптеры не найдены).");
            }

            File.Delete(backupFile);
            return Result.Success("NetBIOS восстановлен из резерва.");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("NetBIOS restore: " + exception.Message);
        }
    }

    // Только чтение: IPv4-интерфейсы с текущим MTU (аналог netsh interface ipv4 show subinterfaces).
    public sealed record InterfaceInfo(string Alias, int Mtu);

    public async Task<Result<IReadOnlyList<InterfaceInfo>>> GetInterfacesAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var interfaces = new List<InterfaceInfo>();
                using var searcher = new ManagementObjectSearcher(
                    "root\\StandardCimv2",
                    "SELECT InterfaceAlias, NlMtu FROM MSFT_NetIPInterface WHERE AddressFamily = 2");
                using var results = searcher.Get();
                foreach (var item in results.OfType<ManagementObject>())
                {
                    using (item)
                    {
                        if (item["InterfaceAlias"] is string alias && alias.Length > 0)
                        {
                            interfaces.Add(new InterfaceInfo(alias, (int)(uint)item["NlMtu"]));
                        }
                    }
                }

                return interfaces
                    .OrderBy(i => i.Alias, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }, ct).ConfigureAwait(false);

            return Result<IReadOnlyList<InterfaceInfo>>.Success(list);
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyList<InterfaceInfo>>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<InterfaceInfo>>.Failure(
                "Не удалось получить список интерфейсов: " + exception.Message);
        }
    }

    // Только чтение: TcpipNetbiosOptions активных IPv4-интерфейсов (0=по DHCP, 1=включён, 2=отключён).
    public static IReadOnlyList<(uint Index, int Options)> GetNetBiosModes() => QueryNetBiosAdapters();

    // nbtstat -R (очистка кэша имён) и -RR (перерегистрация) — пункт 3 меню NetBIOS в BAT.
    public async Task<Result> FlushNetBiosCacheAsync(CancellationToken ct = default)
    {
        var purge = await _runner.RunAsync("nbtstat", ["-R"], null, ct).ConfigureAwait(false);
        var reRegister = await _runner.RunAsync("nbtstat", ["-RR"], null, ct).ConfigureAwait(false);
        _logger.Info($"NET | nbtstat cache reset | rcR={purge.Code} | rcRR={reRegister.Code}");
        return purge.IsSuccess && reRegister.IsSuccess
            ? Result.Success("Кэш NetBIOS сброшен.")
            : Result.Failure("Сброс кэша NetBIOS выполнен не полностью.", purge.IsSuccess ? reRegister.Code : purge.Code);
    }

    // ipconfig /flushdns — очистка кэша DNS-резолвера (локальная история запросов к доменам).
    public async Task<Result> FlushDnsCacheAsync(CancellationToken ct = default)
    {
        var flush = await _runner.RunAsync("ipconfig", ["/flushdns"], null, ct).ConfigureAwait(false);
        _logger.Info($"NET | ipconfig /flushdns | rc={flush.Code}");
        return flush.IsSuccess
            ? Result.Success("Кэш DNS очищен.")
            : Result.Failure("Не удалось очистить кэш DNS.", flush.Code);
    }

    // Универсальный сбалансированный профиль физическим NIC.
    // Используются стандартизированные NDIS RegistryKeyword, поэтому локализация Windows/драйвера
    // не влияет на поиск свойства. Если конкретный драйвер не предоставляет свойство — оно пропускается.
    // Перед применением текущие значения тех же ключей сохраняются в резерв (adapter_profile.txt).
    public async Task<Result> ApplyUniversalAdapterProfileAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var backup = await BackupAdapterProfileAsync(ct).ConfigureAwait(false);
        if (!backup.IsSuccess)
        {
            return backup;
        }

        const string script = """
$ErrorActionPreference = 'Stop'

$settings = @(
    [pscustomobject]@{ Group = 'RSS'; Key = '*RSS'; Value = '1' },
    [pscustomobject]@{ Group = 'Checksum Offload'; Key = '*TCPUDPChecksumOffloadIPv4'; Value = '3'; Fallback = @('*IPChecksumOffloadIPv4','*TCPChecksumOffloadIPv4','*UDPChecksumOffloadIPv4') },
    [pscustomobject]@{ Group = 'Checksum Offload'; Key = '*TCPUDPChecksumOffloadIPv6'; Value = '3'; Fallback = @('*TCPChecksumOffloadIPv6','*UDPChecksumOffloadIPv6') },
    [pscustomobject]@{ Group = 'LSO v2'; Key = '*LsoV2IPv4'; Value = '1' },
    [pscustomobject]@{ Group = 'LSO v2'; Key = '*LsoV2IPv6'; Value = '1' },
    [pscustomobject]@{ Group = 'Interrupt Moderation'; Key = '*InterruptModeration'; Value = '1' },
    [pscustomobject]@{ Group = 'Flow Control'; Key = '*FlowControl'; Value = '3' },
    [pscustomobject]@{ Group = 'Jumbo Packet'; Key = '*JumboPacket'; Value = '1514' },
    [pscustomobject]@{ Group = 'Energy Efficient Ethernet'; Key = '*EEE'; Value = '0' }
)

$adapters = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.Name -and $_.Status -ne 'Not Present' })
if ($adapters.Count -eq 0) {
    Write-Output 'NO_ADAPTERS'
    exit 0
}

foreach ($adapter in $adapters) {
    $name = [string]$adapter.Name
    Write-Output ("ADAPTER|{0}" -f $name)

    try {
        $props = @(Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction Stop)
    }
    catch {
        Write-Output ("ADAPTER_FAIL|{0}|{1}" -f $name, $_.Exception.Message)
        continue
    }

    $changed = $false
    $verifyKeys = New-Object System.Collections.Generic.List[string]

    foreach ($setting in $settings) {
        $candidates = @($setting.Key)
        if ($setting.PSObject.Properties.Name -contains 'Fallback') {
            $candidates += @($setting.Fallback)
        }

        $property = $null
        foreach ($candidate in $candidates) {
            $property = $props | Where-Object { $_.RegistryKeyword -eq $candidate } | Select-Object -First 1
            if ($null -ne $property) {
                break
            }
        }

        if ($null -eq $property) {
            Write-Output ("SETTING|{0}|{1}|SKIP" -f $setting.Group, $setting.Key)
            continue
        }

        try {
            Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $property.RegistryKeyword -RegistryValue $setting.Value -NoRestart -ErrorAction Stop | Out-Null
            $verifyKeys.Add([string]$property.RegistryKeyword) | Out-Null
            $changed = $true
            Write-Output ("SETTING|{0}|{1}|SET" -f $setting.Group, $property.RegistryKeyword)
        }
        catch {
            Write-Output ("SETTING|{0}|{1}|FAIL|{2}" -f $setting.Group, $property.RegistryKeyword, $_.Exception.Message)
        }
    }

    # Interrupt Moderation Rate is vendor-specific. Use it only if the driver exposes Adaptive.
    $rateProperty = $props | Where-Object {
        ($_.DisplayName -and $_.DisplayName -match '(?i)interrupt\s+moderation\s+rate') -or
        ($_.RegistryKeyword -and $_.RegistryKeyword -match '(?i)ITR|InterruptModerationRate')
    } | Select-Object -First 1
    if ($null -ne $rateProperty) {
        $validValues = @($rateProperty.ValidDisplayValues | ForEach-Object { [string]$_ })
        if ($validValues | Where-Object { $_ -ieq 'Adaptive' }) {
            try {
                if ([string]::IsNullOrWhiteSpace([string]$rateProperty.DisplayName)) {
                    Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $rateProperty.RegistryKeyword -DisplayValue 'Adaptive' -NoRestart -ErrorAction Stop | Out-Null
                }
                else {
                    Set-NetAdapterAdvancedProperty -Name $name -DisplayName $rateProperty.DisplayName -DisplayValue 'Adaptive' -NoRestart -ErrorAction Stop | Out-Null
                }
                $changed = $true
                Write-Output ("SETTING|Interrupt Moderation|{0}|SET" -f $(if ([string]::IsNullOrWhiteSpace([string]$rateProperty.DisplayName)) { $rateProperty.RegistryKeyword } else { $rateProperty.DisplayName }))
            }
            catch {
                Write-Output ("SETTING|Interrupt Moderation|Interrupt Moderation Rate|FAIL|{0}" -f $_.Exception.Message)
            }
        }
        else {
            Write-Output 'SETTING|Interrupt Moderation|Interrupt Moderation Rate|SKIP'
        }
    }
    else {
        Write-Output 'SETTING|Interrupt Moderation|Interrupt Moderation Rate|SKIP'
    }

    if ($changed) {
        try {
            Write-Output 'RESTART|BEGIN'
            Restart-NetAdapter -Name $name -Confirm:$false -ErrorAction Stop | Out-Null
        }
        catch {
            Write-Output ("RESTART|FAIL|{0}" -f $_.Exception.Message)
        }

        $verifyProps = @(Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction SilentlyContinue)
        foreach ($key in $verifyKeys) {
            $current = $verifyProps | Where-Object { $_.RegistryKeyword -eq $key } | Select-Object -First 1
            $expected = $settings | Where-Object { $_.Key -eq $key } | Select-Object -First 1
            if ($null -eq $expected -and $key -eq '*IPChecksumOffloadIPv4') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $expected -and $key -eq '*TCPChecksumOffloadIPv4') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $expected -and $key -eq '*UDPChecksumOffloadIPv4') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $expected -and $key -eq '*TCPChecksumOffloadIPv6') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $expected -and $key -eq '*UDPChecksumOffloadIPv6') { $expected = [pscustomobject]@{ Value = '3' } }
            if ($null -eq $current -or $null -eq $expected -or [string]$current.RegistryValue -ne [string]$expected.Value) {
                Write-Output ("VERIFY|{0}|FAIL" -f $key)
            }
            else {
                Write-Output ("VERIFY|{0}|OK" -f $key)
            }
        }
    }
}
""";

        try
        {
            var result = await _runner
                .RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-NoLogo", "-NonInteractive", "-Command", script],
                    progress,
                    ct)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return Result.Failure(result.Message, result.Code);
            }

            var changed = 0;
            var skipped = 0;
            var failed = 0;
            var verifyFailed = 0;
            var adapters = 0;
            var adapterFailures = 0;
            var restartsFailed = 0;
            foreach (var line in (result.Value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line == "NO_ADAPTERS")
                {
                    continue;
                }

                if (line.StartsWith("ADAPTER|", StringComparison.Ordinal))
                {
                    adapters++;
                    continue;
                }

                if (line.StartsWith("ADAPTER_FAIL|", StringComparison.Ordinal))
                {
                    adapterFailures++;
                    continue;
                }

                if (line.StartsWith("SETTING|", StringComparison.Ordinal))
                {
                    if (line.EndsWith("|SET", StringComparison.Ordinal))
                    {
                        changed++;
                    }
                    else if (line.EndsWith("|SKIP", StringComparison.Ordinal))
                    {
                        skipped++;
                    }
                    else if (line.Contains("|FAIL|", StringComparison.Ordinal))
                    {
                        failed++;
                    }
                    continue;
                }

                if (line.StartsWith("VERIFY|", StringComparison.Ordinal) && line.EndsWith("|FAIL", StringComparison.Ordinal))
                {
                    verifyFailed++;
                    continue;
                }

                if (line.StartsWith("RESTART|FAIL|", StringComparison.Ordinal))
                {
                    restartsFailed++;
                }
            }

            if (adapters == 0 && adapterFailures == 0)
            {
                return Result.Success("Физические сетевые адаптеры не найдены.");
            }

            if (failed > 0 || verifyFailed > 0 || adapterFailures > 0 || restartsFailed > 0)
            {
                return Result.Failure(
                    $"Обработано адаптеров: {adapters}; изменено: {changed}; пропущено: {skipped}; ошибок настройки: {failed}; ошибок проверки: {verifyFailed}; ошибок чтения: {adapterFailures}; ошибок перезапуска: {restartsFailed}.");
            }

            return Result.Success(
                $"Универсальный профиль применён: адаптеров {adapters}; изменено {changed}; пропущено {skipped} неподдерживаемых свойств.");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось применить универсальный профиль: " + exception.Message);
        }
    }

    // Резерв расширенных свойств физических адаптеров ДО применения профиля.
    private const string AdapterProfileBackupScript = """
$ErrorActionPreference = 'Stop'

$keys = @(
    '*RSS',
    '*TCPUDPChecksumOffloadIPv4',
    '*TCPUDPChecksumOffloadIPv6',
    '*IPChecksumOffloadIPv4',
    '*TCPChecksumOffloadIPv4',
    '*TCPChecksumOffloadIPv6',
    '*UDPChecksumOffloadIPv4',
    '*UDPChecksumOffloadIPv6',
    '*LsoV2IPv4',
    '*LsoV2IPv6',
    '*InterruptModeration',
    '*FlowControl',
    '*JumboPacket',
    '*EEE'
)

$adapters = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.Name -and $_.Status -ne 'Not Present' })
if ($adapters.Count -eq 0) {
    Write-Output 'NO_ADAPTERS'
    exit 0
}

foreach ($adapter in $adapters) {
    $name = [string]$adapter.Name
    try {
        $props = @(Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction Stop)
    }
    catch {
        Write-Output ("BACKUP_FAIL|{0}|{1}" -f $name, $_.Exception.Message)
        continue
    }

    foreach ($key in $keys) {
        $property = $props | Where-Object { $_.RegistryKeyword -eq $key } | Select-Object -First 1
        if ($null -ne $property) {
            Write-Output ("ADAPTER|{0}|{1}|{2}" -f $name, $key, [string]$property.RegistryValue)
        }
        else {
            Write-Output ("ADAPTER|{0}|{1}|-" -f $name, $key)
        }
    }

    # Interrupt Moderation Rate is vendor-specific; back up its raw registry value by keyword.
    $rateProperty = $props | Where-Object {
        ($_.DisplayName -and $_.DisplayName -match '(?i)interrupt\s+moderation\s+rate') -or
        ($_.RegistryKeyword -and $_.RegistryKeyword -match '(?i)ITR|InterruptModerationRate')
    } | Select-Object -First 1
    if ($null -ne $rateProperty) {
        Write-Output ("ADAPTER|{0}|{1}|{2}" -f $name, $rateProperty.RegistryKeyword, [string]$rateProperty.RegistryValue)
    }
}
""";

    // Выгрузка резерва профиля: PS печатает строки "ADAPTER|<имя>|<keyword>|<значение|->"
    // ("-" — свойства нет), файл пишет C#. Кодировка UTF8 с BOM — иначе Windows PowerShell 5.1
    // прочитает кириллические имена адаптеров в ANSI и откат по ним не найдёт адаптер.
    // Повторное применение резерв не перезаписывает: в нём значения ДО первой правки.
    private async Task<Result> BackupAdapterProfileAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(AdapterProfileBackupFile))
            {
                return Result.Success("Резерв уже существует.");
            }

            var result = await _runner
                .RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-NoLogo", "-NonInteractive", "-Command", AdapterProfileBackupScript],
                    null,
                    ct)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Result.Failure("Не удалось выгрузить текущие свойства адаптеров: " + result.Message, result.Code);
            }

            var lines = (result.Value ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("ADAPTER|", StringComparison.Ordinal))
                .ToList();
            if (lines.Count == 0)
            {
                // Адаптеров нет — резервировать нечего; применение профиля само сообщит об этом.
                return Result.Success("Физические сетевые адаптеры не найдены.");
            }

            Directory.CreateDirectory(BackupDirectory);
            File.WriteAllLines(AdapterProfileBackupFile, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _logger.Info($"NET | adapter profile backup | {lines.Count} строк");
            return Result.Success("Резерв сохранён.");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось сохранить резерв свойств адаптеров: " + exception.Message);
        }
    }

    // Откат профиля: Set-NetAdapterAdvancedProperty по значениям из резерва (значение "-" —
    // свойства не было, пропускается), затем перезапуск изменённых адаптеров.
    // После успеха резерв удаляется (best-effort: сбой удаления не роняет операцию).
    public async Task<Result> RestoreAdapterProfileAsync(CancellationToken ct = default)
    {
        if (!File.Exists(AdapterProfileBackupFile))
        {
            return Result.Success("Резерв свойств адаптеров отсутствует — восстанавливать нечего.");
        }

        var script = AdapterProfileRestoreScript.Replace(
            "<BACKUP_PATH>",
            AdapterProfileBackupFile.Replace("'", "''", StringComparison.Ordinal),
            StringComparison.Ordinal);

        try
        {
            var result = await _runner
                .RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-NoLogo", "-NonInteractive", "-Command", script],
                    null,
                    ct)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Result.Failure(result.Message, result.Code);
            }

            var restored = 0;
            var failures = 0;
            var restartsFailed = 0;
            foreach (var line in (result.Value ?? string.Empty)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("RESTORE|", StringComparison.Ordinal))
                {
                    if (line.EndsWith("|OK", StringComparison.Ordinal))
                    {
                        restored++;
                    }
                    else
                    {
                        failures++;
                    }
                }
                else if (line.StartsWith("RESTART|FAIL|", StringComparison.Ordinal))
                {
                    restartsFailed++;
                }
            }

            if (failures > 0 || restartsFailed > 0)
            {
                return Result.Failure(
                    $"Свойства адаптеров восстановлены не полностью: значений {restored}, ошибок {failures}, ошибок перезапуска {restartsFailed}; резерв сохранён.");
            }

            // Удаление резерва — необязательный хвост: не роняет успешную операцию.
            try
            {
                File.Delete(AdapterProfileBackupFile);
            }
            catch (Exception exception)
            {
                _logger.Warn("NET | adapter profile backup cleanup failed: " + exception.Message);
            }

            _logger.Info($"NET | adapter profile restored | {restored} значений");
            return Result.Success($"Свойства адаптеров восстановлены из резерва ({restored} значений).");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось восстановить свойства адаптеров: " + exception.Message);
        }
    }

    private const string AdapterProfileRestoreScript = """
$ErrorActionPreference = 'Continue'

$backupPath = '<BACKUP_PATH>'
if (-not (Test-Path -LiteralPath $backupPath)) {
    Write-Output 'NO_BACKUP'
    exit 0
}

$restart = New-Object System.Collections.Generic.List[string]
foreach ($line in [System.IO.File]::ReadAllLines($backupPath)) {
    $parts = $line -split '\|', 4
    if ($parts.Count -ne 4 -or $parts[0] -ne 'ADAPTER') {
        continue
    }

    $name = $parts[1]
    $key = $parts[2]
    $value = $parts[3]
    if ($key -eq '-' -or $value -eq '-') {
        continue
    }

    try {
        Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $key -RegistryValue $value -NoRestart -ErrorAction Stop | Out-Null
        Write-Output ("RESTORE|{0}|{1}|OK" -f $name, $key)
        if (-not $restart.Contains($name)) {
            $restart.Add($name)
        }
    }
    catch {
        Write-Output ("RESTORE|{0}|{1}|FAIL|{2}" -f $name, $key, $_.Exception.Message)
    }
}

foreach ($name in $restart) {
    try {
        Write-Output 'RESTART|BEGIN'
        Restart-NetAdapter -Name $name -Confirm:$false -ErrorAction Stop | Out-Null
    }
    catch {
        Write-Output ("RESTART|FAIL|{0}" -f $_.Exception.Message)
    }
}
""";

    // Признак «есть что откатывать» — используется VM для доступности кнопки «Откатить».
    public static bool HasAdapterProfileBackup() => File.Exists(AdapterProfileBackupFile);

    private static string AdapterProfileBackupFile => Path.Combine(BackupDirectory, "adapter_profile.txt");

    // Игровой профиль: Auto-Tuning=disabled, ECN=disabled, QoS override=0.
    public async Task<Result> ApplyGamingProfileAsync(CancellationToken ct = default)
    {
        var tcpBackup = await BackupTcpGlobalAsync(ct).ConfigureAwait(false);
        if (!tcpBackup.IsSuccess)
        {
            return Result.Failure("Не удалось сохранить TCP Global — профиль отменён.", tcpBackup.Code);
        }

        var qosBackup = SetQosOverride(0);
        if (!qosBackup.IsSuccess)
        {
            return Result.Failure("Не удалось сохранить/установить QoS — профиль отменён: " + qosBackup.Message);
        }

        var autoTuning = await SetTcpGlobalAsync("autotuninglevel", "disabled", ct).ConfigureAwait(false);
        var ecn = await SetTcpGlobalAsync("ecncapability", "disabled", ct).ConfigureAwait(false);
        // Сообщения об ошибках берём только у неуспешных результатов (раньше фильтровали
        // по отсутствию '=', из-за чего часть честных ошибок выпадала из отчёта).
        var failures = new[] { autoTuning, ecn }
            .Where(result => !result.IsSuccess)
            .Select(result => result.Message)
            .ToList();
        if (failures.Count > 0)
        {
            return Result.Failure("Профиль применён частично: " + string.Join("; ", failures));
        }

        _logger.Info("NET | gaming profile applied");
        return Result.Success("Игровой профиль применён (Auto-Tuning=disabled, ECN=disabled, QoS=0%).");
    }

    public async Task<Result> ResetAllAsync(CancellationToken ct = default)
    {
        var results = new List<Result>
        {
            await RestoreTcpGlobalAsync(ct).ConfigureAwait(false),
            RestoreQosOverride(),
            await RestoreMtuAsync(ct).ConfigureAwait(false),
            await RestoreNetBiosAsync(ct).ConfigureAwait(false)
        };

        var failed = results.Where(r => !r.IsSuccess).ToList();
        if (failed.Count > 0)
        {
            return Result.Failure("Сброс завершён не полностью: " + string.Join("; ", failed.Select(r => r.Message)));
        }

        return Result.Success("Сброс завершён: TCP Global, QoS, MTU и NetBIOS восстановлены.");
    }

    private async Task<Result> BackupTcpGlobalAsync(CancellationToken ct)
    {
        if (File.Exists(TcpGlobalBackupFile))
        {
            return Result.Success("Резерв уже существует.");
        }

        var state = await GetTcpGlobalAsync(ct).ConfigureAwait(false);
        if (!state.IsSuccess)
        {
            return Result.Failure(state.Message, state.Code);
        }

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            File.WriteAllLines(TcpGlobalBackupFile, [$"autotuning={state.Value?.AutoTuning}", $"ecn={state.Value?.Ecn}"]);
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось записать резерв TCP Global: " + exception.Message);
        }

        _logger.Info($"NET | TCP Global backup | auto={state.Value?.AutoTuning} ecn={state.Value?.Ecn}");
        return Result.Success("Резерв сохранён.");
    }

    private async Task<Result<int>> GetCurrentMtuAsync(string interfaceAlias, CancellationToken ct)
    {
        try
        {
            var current = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var searcher = new ManagementObjectSearcher(
                    "root\\StandardCimv2",
                    "SELECT InterfaceAlias, NlMtu FROM MSFT_NetIPInterface WHERE AddressFamily = 2");
                using var results = searcher.Get();
                foreach (var item in results.OfType<ManagementObject>())
                {
                    using (item)
                    {
                        if (string.Equals((string)item["InterfaceAlias"], interfaceAlias, StringComparison.OrdinalIgnoreCase))
                        {
                            return (int?)(uint)item["NlMtu"];
                        }
                    }
                }

                return (int?)null;
            }, ct).ConfigureAwait(false);

            return current is null
                ? Result<int>.Failure($"Интерфейс «{interfaceAlias}» не найден.")
                : Result<int>.Success(current.Value);
        }
        catch (OperationCanceledException)
        {
            // Единый контракт раздела: отмена — Failure(-1), а не исключение,
            // вылетающее за пределы Result-обработки вызывающего.
            return Result<int>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result<int>.Failure("Не удалось прочитать текущий MTU: " + exception.Message);
        }
    }

    private async Task<Result> BackupMtuAsync(string interfaceAlias, CancellationToken ct)
    {
        var current = await GetCurrentMtuAsync(interfaceAlias, ct).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return Result.Failure(current.Message, current.Code);
        }

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var line = $"{interfaceAlias}\t{current.Value!.ToString(CultureInfo.InvariantCulture)}";
            var existing = File.Exists(MtuBackupFile) ? File.ReadAllLines(MtuBackupFile).ToList() : [];
            if (!existing.Contains(line))
            {
                existing.Add(line);
                File.WriteAllLines(MtuBackupFile, existing);
            }
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось записать резерв MTU: " + exception.Message);
        }

        _logger.Info($"NET | MTU backup | {interfaceAlias}={current.Value}");
        return Result.Success("Резерв сохранён.");
    }

    private static IReadOnlyList<(uint Index, int Options)> QueryNetBiosAdapters()
    {
        var result = new List<(uint, int)>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Index, TcpipNetbiosOptions FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
        using var results = searcher.Get();
        foreach (var adapter in results.OfType<ManagementObject>())
        {
            var options = adapter["TcpipNetbiosOptions"];
            result.Add((Convert.ToUInt32(adapter["Index"]), options is null ? 0 : Convert.ToInt32(options)));
            adapter.Dispose();
        }

        return result;
    }

    private static string TcpGlobalBackupFile => Path.Combine(BackupDirectory, "tcp_global.txt");
    private static string MtuBackupFile => Path.Combine(BackupDirectory, "mtu.txt");
    private static string QosBackupFile => Path.Combine(BackupDirectory, "qos.txt");
    private static string NetBiosBackupFile => Path.Combine(BackupDirectory, "netbios.txt");
}
