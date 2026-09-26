using System.Globalization;
using System.Reflection;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.Services;

// Агрегатор состояния ПК для раздела «Состояние ПК» (Dashboard).
// Только чтение: источники — существующие сервисы; тяжёлые диагностики (DISM/SFC)
// здесь не запускаются. Каждая область — под собственным try/catch: сбой одной
// области фиксируется в AreaErrors и не ломает остальные (аналог RunSectionInitAsync).
public sealed class SystemStateService
{
    // Ключи областей: прогресс Smart Scan и ключи AreaErrors.
    public const string AreaSystem = "system";
    public const string AreaDisk = "disk";
    public const string AreaStartup = "startup";
    public const string AreaServices = "services";
    public const string AreaTasks = "tasks";
    public const string AreaNetwork = "network";
    public const string AreaPower = "power";
    public const string AreaSecurity = "security";
    public const string AreaPrivacy = "privacy";
    public const string AreaUpdates = "updates";

    // Нейтральные значения UacState (локализация — на слое отображения).
    public const string UacStandard = "standard";
    public const string UacWeakened = "weakened";

    private readonly Logger _logger;
    private readonly SystemInfoService _systemInfo;
    private readonly ServiceManager _services;
    private readonly TaskManager _tasks;
    private readonly NetworkService _network;
    private readonly PowerService _power;
    private readonly SecurityService _security;
    private readonly PrivacyService _privacy;
    private readonly SCURunner _runner;

    public SystemStateService(Logger logger, SCURunner runner)
    {
        _logger = logger;
        _runner = runner;
        var registry = new RegistryHelper(logger);
        var longRunner = new LongProcessRunner(logger);
        _systemInfo = new SystemInfoService();
        _services = new ServiceManager();
        _tasks = new TaskManager(runner);
        _network = new NetworkService(logger, longRunner);
        _power = new PowerService(logger, longRunner, registry);
        _security = new SecurityService(logger, registry);
        _privacy = new PrivacyService(logger, registry, _services, runner);
    }

    // Быстрый снимок: SystemInfo (WMI), активная схема питания и флаги обновлений.
    // Остальные поля остаются null — их даёт только полная проверка.
    public async Task<SystemSnapshot> CollectLightweightAsync(CancellationToken ct = default)
    {
        var snapshot = new SystemSnapshot { Timestamp = DateTime.Now, AreaErrors = [] };

        var info = await _systemInfo.GetAsync(ct).ConfigureAwait(false);
        if (info.IsSuccess && info.Value is not null)
        {
            ApplySystemInfo(snapshot, info.Value);
            snapshot.Disks = ParseDisks(info.Value);
        }
        else
        {
            snapshot.AreaErrors[AreaSystem] = info.Message;
        }

        var plan = await _power.GetActivePlanGuidAsync(ct).ConfigureAwait(false);
        if (plan.IsSuccess && plan.Value is not null)
        {
            snapshot.ActivePlanGuid = plan.Value;
        }

        // Чтения реестра — быстрые и не падают (внутри try/catch возвращают false).
        snapshot.UpdateBlocked = UpdateService.IsBlocked();
        snapshot.UpdatePaused = UpdateService.IsPaused();

        return snapshot;
    }

    // Полная проверка по областям, последовательно (прогресс — по мере готовности).
    // OperationCanceledException прерывает всю проверку: пользователь отменил.
    public async Task<SystemSnapshot> CollectFullSnapshotAsync(
        CancellationToken ct = default,
        IProgress<(string Area, AreaScanState State)>? progress = null)
    {
        var snapshot = new SystemSnapshot { Timestamp = DateTime.Now, AreaErrors = [] };
        SystemInfo? systemInfo = null;

        await RunAreaAsync(snapshot, progress, AreaSystem, async () =>
        {
            var info = await _systemInfo.GetAsync(ct).ConfigureAwait(false);
            if (!info.IsSuccess || info.Value is null)
            {
                throw new InvalidOperationException(info.Message);
            }

            systemInfo = info.Value;
            ApplySystemInfo(snapshot, systemInfo);
        }).ConfigureAwait(false);

        // Диски берутся из уже прочитанной SystemInfo — второй WMI-проход не нужен.
        await RunAreaAsync(snapshot, progress, AreaDisk, async () =>
        {
            if (systemInfo is null)
            {
                throw new InvalidOperationException("Системная информация недоступна — диски не прочитаны.");
            }

            snapshot.Disks = ParseDisks(systemInfo);
        }).ConfigureAwait(false);

        await RunAreaAsync(snapshot, progress, AreaStartup, async () =>
        {
            // Тот же механизм и тот же файл кэша, что у StartupViewModel (StartupList ps1).
            var outFile = Path.Combine(GetCacheDirectory(), "startup_list.txt");
            var result = await _runner.RunAsync(
                "StartupList",
                new Dictionary<string, string?> { ["OutFile"] = outFile },
                progress: null,
                ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Message);
            }

            snapshot.StartupCount = CountStartupItems(outFile);
        }).ConfigureAwait(false);

        await RunAreaAsync(snapshot, progress, AreaServices, async () =>
        {
            var result = await _services.GetDefaultServicesAsync(ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value is null)
            {
                throw new InvalidOperationException(result.Message);
            }

            var list = result.Value;
            snapshot.ServicesTotal = list.Count;
            // «Изменённые» = запуск запрещён (Disabled), а не runtime status.
            snapshot.ServicesChanged = list.Count(service =>
                service.QueryStatus == ServiceQueryStatus.Ok
                && service.StartupType == ServiceStartupType.Disabled);
            // «Ок» = служба прочитана и запуск разрешён (независимо от Running/Stopped).
            snapshot.ServicesOk = list.Count(service =>
                service.QueryStatus == ServiceQueryStatus.Ok
                && service.StartupType != ServiceStartupType.Disabled);
        }).ConfigureAwait(false);

        await RunAreaAsync(snapshot, progress, AreaTasks, async () =>
        {
            // Отдельный файл кэша Dashboard, чтобы не пересекаться с TasksViewModel.
            var outFile = Path.Combine(GetCacheDirectory(), "dashboard_tasks_list.txt");
            var result = await _tasks.ListAsync(outFile, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value is null)
            {
                throw new InvalidOperationException(result.Message);
            }

            var list = result.Value;
            snapshot.TasksTotal = list.Count;
            snapshot.TasksDisabled = list.Count(task => task.IsDisabled);
        }).ConfigureAwait(false);

        await RunAreaAsync(snapshot, progress, AreaNetwork, async () =>
        {
            var autoTuning = (string?)null;
            var ecn = (string?)null;
            var tcp = await _network.GetTcpGlobalAsync(ct).ConfigureAwait(false);
            if (tcp.IsSuccess && tcp.Value is not null)
            {
                autoTuning = tcp.Value.AutoTuning;
                ecn = tcp.Value.Ecn;
            }

            var qos = await TaskRunner.RunBlocking(() =>
            {
                var value = _network.GetQosOverride();
                return value is null ? null : value.Value.ToString(CultureInfo.InvariantCulture) + "%";
            }, ct).ConfigureAwait(false);

            var netBios = await TaskRunner.RunBlocking(() =>
            {
                var modes = NetworkService.GetNetBiosModes();
                return modes.Count == 0
                    ? null
                    : string.Join(", ", modes.Select(mode => mode.Options).Distinct());
            }, ct).ConfigureAwait(false);

            if (autoTuning is null && ecn is null && qos is null && netBios is null)
            {
                throw new InvalidOperationException("Не удалось прочитать сетевые параметры.");
            }

            snapshot.Network = new NetworkSummarySnapshot(autoTuning, ecn, qos, netBios);
        }).ConfigureAwait(false);

        await RunAreaAsync(snapshot, progress, AreaPower, async () =>
        {
            var plan = await _power.GetActivePlanGuidAsync(ct).ConfigureAwait(false);
            if (!plan.IsSuccess || plan.Value is null)
            {
                throw new InvalidOperationException(plan.Message);
            }

            snapshot.ActivePlanGuid = plan.Value;
        }).ConfigureAwait(false);

        await RunAreaAsync(snapshot, progress, AreaSecurity, async () =>
        {
            var standard = await TaskRunner.RunBlocking(() => _security.IsStandard(), ct)
                .ConfigureAwait(false);
            snapshot.UacState = standard ? UacStandard : UacWeakened;
        }).ConfigureAwait(false);

        await RunAreaAsync(snapshot, progress, AreaPrivacy, async () =>
        {
            var counts = await TaskRunner.RunBlocking(() =>
            {
                var applied = PrivacyService.Categories.Count(category =>
                    _privacy.IsCategoryApplied(category.Id));
                return (applied, total: PrivacyService.Categories.Count);
            }, ct).ConfigureAwait(false);
            snapshot.PrivacyAppliedCount = counts.applied;
            snapshot.PrivacyTotal = counts.total;
        }).ConfigureAwait(false);

        await RunAreaAsync(snapshot, progress, AreaUpdates, async () =>
        {
            snapshot.UpdateBlocked = UpdateService.IsBlocked();
            snapshot.UpdatePaused = UpdateService.IsPaused();
        }).ConfigureAwait(false);

        return snapshot;
    }

    // Одна область: Running → действие → Ok/Failed. Отмена пробрасывается выше.
    private async Task RunAreaAsync(
        SystemSnapshot snapshot,
        IProgress<(string Area, AreaScanState State)>? progress,
        string area,
        Func<Task> action)
    {
        progress?.Report((area, AreaScanState.Running));
        try
        {
            await action().ConfigureAwait(false);
            progress?.Report((area, AreaScanState.Ok));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            snapshot.AreaErrors[area] = exception.Message;
            _logger.Warn("SCAN | area failed | " + area + " | " + exception.Message);
            progress?.Report((area, AreaScanState.Failed));
        }
    }

    private static void ApplySystemInfo(SystemSnapshot snapshot, SystemInfo info)
    {
        snapshot.AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        snapshot.WindowsVersion = info.OsCaption;
        snapshot.WindowsBuild = info.OsVersion;
        snapshot.Cpu = info.CpuSummary;
        snapshot.Ram = info.RamTotalBytes is { } ramBytes ? FormatGb(ramBytes) : null;
        snapshot.Gpu = info.VideoAdapters.Count > 0 ? string.Join("; ", info.VideoAdapters) : null;
    }

    private static string FormatGb(ulong bytes) =>
        (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " ГБ";

    // Числа дисков — из типизированных TotalBytes/FreeBytes DiskInfo (строки Total/Free
    // существуют только для отображения). Нулевой Total → диск пропускается.
    private static IReadOnlyList<DiskSnapshot> ParseDisks(SystemInfo info)
    {
        var disks = new List<DiskSnapshot>();
        foreach (var disk in info.Disks)
        {
            if (disk.TotalBytes == 0)
            {
                continue;
            }

            var totalGb = disk.TotalBytes / (1024d * 1024 * 1024);
            var freeGb = disk.FreeBytes / (1024d * 1024 * 1024);
            var usedPercent = disk.UsedPercent == "—"
                ? (double?)null
                : (totalGb - freeGb) * 100.0 / totalGb;

            disks.Add(new DiskSnapshot(disk.Letter, totalGb, freeGb, usedPercent));
        }

        return disks;
    }

    // Число элементов автозагрузки из файла StartupList ("index\tsource\tname\tcmd"),
    // без мусорных записей — та же разборка, что в StartupViewModel.ParseList/IsJunk.
    private static int? CountStartupItems(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var count = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length < 4)
                {
                    continue;
                }

                var name = parts[2].Trim();
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                count++;
            }

            return count;
        }
        catch
        {
            return null;
        }
    }

    private static string GetCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU",
        "cache");
}
