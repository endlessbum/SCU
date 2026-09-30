using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;

namespace SCU.Infrastructure.Windows.Tweaks;

// Категории приватности из раздела 5 «Utilities.bat» (телеметрия, уведомления, UWP, Copilot,
// оптимизация доставки). Реестр — через RegistryHelper с резервом; службы — через ServiceManager;
// задачи CEIP — через SCU.ps1 (TasksBackup / TasksDisable / TasksRestore).
public sealed class PrivacyService
{
    public sealed record Category(string Id, string Title, string Description);

    public static IReadOnlyList<Category> Categories { get; } =
    [
        new Category("telemetry", "Телеметрия и реклама",
            "DiagTrack, AllowTelemetry, CEIP, рекламный ID, советы и контент."),
        new Category("notifications", "Уведомления и советы",
            "Тосты, центр уведомлений, советы и предложения Windows."),
        new Category("uwp", "Фоновые UWP-приложения",
            "Глобальный запрет фоновой активности UWP и служба embeddedmode."),
        new Category("copilot", "Windows Copilot AI",
            "Policy-отключение Copilot и анализа данных AI."),
        new Category("do", "Оптимизация доставки",
            "DODownloadMode=0 и служба DoSvc."),
        new Category("ceip", "Планировщик: телеметрия / CEIP",
            "Отключение задач Compatibility Appraiser, Consolidator, QueueReporting и др.")
    ];

    private const string CeipTasksCsv =
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser" +
        @";\Microsoft\Windows\Application Experience\ProgramDataUpdater" +
        @";\Microsoft\Windows\Application Experience\StartupAppTask" +
        @";\Microsoft\Windows\Customer Experience Improvement Program\Consolidator" +
        @";\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip" +
        @";\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector" +
        @";\Microsoft\Windows\Feedback\Siuf\DmClient" +
        @";\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload" +
        @";\Microsoft\Windows\Windows Error Reporting\QueueReporting" +
        @";\Microsoft\Windows\Autochk\Proxy" +
        @";\Microsoft\Windows\PI\Sqm-Tasks" +
        @";\Microsoft\Windows\NetTrace\GatherNetworkInfo" +
        @";\Microsoft\Windows\CloudExperienceHost\CreateObjectTask" +
        @";\Microsoft\Windows\Maps\MapsUpdateTask" +
        @";\Microsoft\Windows\Maps\MapsToastTask";

    private static readonly IReadOnlyList<RegistryTweak> TelemetryTweaks =
    [
        Policy(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
        Policy(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
        Policy(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0),
        Cdm("SubscribedContent-338389Enabled"),
        Cdm("SubscribedContent-338387Enabled"),
        Cdm("SubscribedContent-338393Enabled"),
        Cdm("SubscribedContent-353694Enabled"),
        Cdm("SubscribedContent-353696Enabled"),
        Cdm("SubscribedContent-310093Enabled"),
        Cdm("SystemPaneSuggestionsEnabled"),
        Cdm("SilentInstalledAppsEnabled"),
        Policy(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "AITEnable", 0),
        Policy(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
        Policy(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
        Policy(RegistryHive.CurrentUser, @"Software\Microsoft\Input\TIPC", "Enabled", 0),
        Policy(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\TabletPC", "PreventHandwritingDataSharing", 1),
        Policy(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports", "PreventHandwritingErrorReports", 1),
        Policy(RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location",
            "Value", RegistryValueKind.String, "Deny", null),
        Policy(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreenCamera", 1),
        Policy(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1)
    ];

    private static readonly IReadOnlyList<RegistryTweak> NotificationTweaks =
    [
        Cdm("SubscribedContent-338389Enabled"),
        Cdm("SubscribedContent-338393Enabled"),
        Cdm("SubscribedContent-353694Enabled"),
        Cdm("SubscribedContent-353696Enabled"),
        Cdm("SoftLandingEnabled"),
        Cdm("SystemPaneSuggestionsEnabled"),
        Policy(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
        Policy(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
        new RegistryTweak(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\PushNotifications",
            "ToastEnabled", RegistryValueKind.DWord, 0, 1),
        Policy(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter", 1)
    ];

    private static readonly IReadOnlyList<RegistryTweak> BackgroundUwpTweaks =
    [
        new RegistryTweak(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
            "GlobalUserDisabled", RegistryValueKind.DWord, 1, 0),
        new RegistryTweak(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Search",
            "BackgroundAppGlobalToggle", RegistryValueKind.DWord, 0, 1),
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services\embeddedmode",
            "Start", RegistryValueKind.DWord, 4, 3)
    ];

    private static readonly IReadOnlyList<RegistryTweak> CopilotTweaks =
    [
        Policy(RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
        Policy(RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)
    ];

    private static readonly IReadOnlyList<RegistryTweak> DeliveryOptimizationTweaks =
    [
        Policy(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0)
    ];

    private readonly Logger _logger;
    private readonly RegistryHelper _registry;
    private readonly ServiceManager _serviceManager;
    private readonly SCURunner? _runner;

    public PrivacyService(
        Logger logger,
        RegistryHelper registry,
        ServiceManager serviceManager,
        SCURunner? runner = null)
    {
        _logger = logger;
        _registry = registry;
        _serviceManager = serviceManager;
        _runner = runner;
    }

    private static string BackupDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "privacy");

    private static string TasksBackupFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "tasks", "ceip_tasks_backup.txt");

    public IReadOnlyList<RegistryTweak> GetTweaks(string categoryId) => categoryId switch
    {
        "telemetry" => TelemetryTweaks,
        "notifications" => NotificationTweaks,
        "uwp" => BackgroundUwpTweaks,
        "copilot" => CopilotTweaks,
        "do" => DeliveryOptimizationTweaks,
        _ => []
    };

    public bool IsCategoryApplied(string categoryId)
    {
        // CEIP управляется задачами планировщика: проверяем фактическое состояние
        // задач, а не наличие файла резерва — резерв писался ДО отключения, и после
        // сбоя SCU.ps1 категория считалась применённой, хотя задачи были включены.
        if (categoryId == "ceip")
        {
            return AreCeipTasksDisabled();
        }

        var tweaks = GetTweaks(categoryId);
        return tweaks.Count > 0 && _registry.IsApplied(tweaks);
    }

    // Категория применена, если каждая существующая CEIP-задача отключена
    // (<Enabled>false</Enabled> в XML задачи). Отсутствующая задача неактивна
    // и не мешает; ошибка чтения → не применено (fail-closed для метрики).
    private static bool AreCeipTasksDisabled()
    {
        try
        {
            var tasksRoot = Path.Combine(Environment.SystemDirectory, "Tasks");
            foreach (var task in CeipTasksCsv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var relative = task.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar);
                var file = Path.Combine(tasksRoot, relative);
                if (!File.Exists(file))
                {
                    continue;
                }

                var xml = File.ReadAllText(file);
                if (!xml.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Result> DisableAsync(string categoryId, CancellationToken ct = default)
    {
        // CEIP — задачи планировщика, у категории нет реестровых твиков.
        if (categoryId == "ceip")
        {
            return _runner is not null
                ? await DisableCeipTasksAsync(_runner, ct).ConfigureAwait(false)
                : Result.Failure("Категория ceip требует SCURunner (не передан в конструктор).", 2);
        }

        var tweaks = GetTweaks(categoryId);
        if (tweaks.Count == 0)
        {
            return Result.Failure($"Неизвестная категория: {categoryId}", 2);
        }

        var registryResult = _registry.Apply(tweaks, Path.Combine(BackupDirectory, categoryId + ".json"));
        if (!registryResult.IsSuccess)
        {
            return registryResult;
        }

        var serviceResult = categoryId switch
        {
            "telemetry" => await _serviceManager.SetDisabledAsync("DiagTrack", true, ct).ConfigureAwait(false),
            "do" => await _serviceManager.SetDisabledAsync("DoSvc", true, ct).ConfigureAwait(false),
            _ => Result.Success()
        };
        if (!serviceResult.IsSuccess)
        {
            _logger.Error($"PRIVACY | {categoryId} | service: {serviceResult.Message}");
            return Result.Failure(registryResult.Message + " Служба: " + serviceResult.Message);
        }

        return Result.Success("Параметры применены и проверены. " + serviceResult.Message);
    }

    public async Task<Result> EnableAsync(string categoryId, CancellationToken ct = default)
    {
        if (categoryId == "ceip")
        {
            return _runner is not null
                ? await RestoreCeipTasksAsync(_runner, ct).ConfigureAwait(false)
                : Result.Failure("Категория ceip требует SCURunner (не передан в конструктор).", 2);
        }

        var tweaks = GetTweaks(categoryId);
        if (tweaks.Count == 0)
        {
            return Result.Failure($"Неизвестная категория: {categoryId}", 2);
        }

        var serviceResult = categoryId switch
        {
            "telemetry" => await _serviceManager.SetAutomaticAsync("DiagTrack", ct).ConfigureAwait(false),
            "do" => await _serviceManager.SetAutomaticAsync("DoSvc", ct).ConfigureAwait(false),
            _ => Result.Success()
        };
        if (!serviceResult.IsSuccess)
        {
            return serviceResult;
        }

        // Если категорию отключали не через приложение — резерва нет.
        // Тогда «включить» = вернуть заводское состояние (удалить значения), а не падать.
        var backupFile = Path.Combine(BackupDirectory, categoryId + ".json");
        var restoreResult = File.Exists(backupFile)
            ? _registry.Restore(tweaks, backupFile)
            : _registry.ResetToDefault(tweaks);
        if (!restoreResult.IsSuccess)
        {
            return restoreResult;
        }

        return Result.Success("Включено. " + restoreResult.Message + (serviceResult.Message.Length > 0 ? " " + serviceResult.Message : string.Empty));
    }

    // CEIP-задачи: отключение через PS с резервом, включение из резерва.
    public async Task<Result> DisableCeipTasksAsync(SCURunner runner, CancellationToken ct = default)
    {
        // Повторное отключение не перезаписывает резерв: первый манifест = исходное состояние задач.
        if (File.Exists(TasksBackupFile))
        {
            return Result.Success("Резерв задач уже существует; задачи уже отключены.");
        }

        return await runner.RunAsync(
            "TasksDisable",
            new Dictionary<string, string?>
            {
                ["Tasks"] = CeipTasksCsv,
                ["OutFile"] = TasksBackupFile
            },
            null,
            ct).ConfigureAwait(false);
    }

    public async Task<Result> RestoreCeipTasksAsync(SCURunner runner, CancellationToken ct = default)
    {
        if (!File.Exists(TasksBackupFile))
        {
            return Result.Failure("Резерв задач CEIP не создавался — включение недоступно.", 2);
        }

        var result = await runner.RunAsync(
            "TasksRestore",
            new Dictionary<string, string?> { ["InFile"] = TasksBackupFile },
            null,
            ct).ConfigureAwait(false);

        // Задачи восстановлены — резерв больше не означает «отключено».
        if (result.IsSuccess)
        {
            try
            {
                File.Delete(TasksBackupFile);
            }
            catch
            {
                // Не критично: статус обновится при следующем отключении.
            }
        }

        return result;
    }

    // Policy-твик: OffValue пишется, на включении значение удаляется (заводское поведение).
    private static RegistryTweak Policy(RegistryHive hive, string subKey, string valueName, int offValue) =>
        new(hive, subKey, valueName, RegistryValueKind.DWord, offValue, null);

    // Твик ключа ContentDeliveryManager: OffValue=0, включение — удаление (как reg delete в BAT).
    private static RegistryTweak Cdm(string valueName) =>
        new(
            RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
            valueName,
            RegistryValueKind.DWord,
            0,
            null);
}
