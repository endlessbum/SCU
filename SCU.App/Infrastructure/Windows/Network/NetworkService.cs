using System.Globalization;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;

namespace SCU.Infrastructure.Windows.Network;

// Раздел 9 «Utilities.bat» — сеть: TCP Global (Auto-Tuning, ECN), MTU, QoS override, NetBIOS; профиль NIC — фасад над AdapterProfileManager (п. 13 аудита, B4).
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

    // П. 13 аудита (B4): зона «профиль адаптера NIC» перенесена в AdapterProfileManager.
    // Фасад сохранён — NetworkViewModel и статический признак бэкапа вызываются как раньше.
    public Task<Result> ApplyUniversalAdapterProfileAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => new AdapterProfileManager(_logger, _runner).ApplyUniversalAdapterProfileAsync(progress, ct);

    public Task<Result> RestoreAdapterProfileAsync(CancellationToken ct = default)
        => new AdapterProfileManager(_logger, _runner).RestoreAdapterProfileAsync(ct);

    public static bool HasAdapterProfileBackup() => AdapterProfileManager.HasAdapterProfileBackup();

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
