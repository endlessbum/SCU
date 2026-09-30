using System.Text.Json;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// System/SCU state tools (п. 8C ТЗ) — read-only. Состояние читается у тех же VM,
// которыми пользуется UI: второго способа чтения нет (п. 8/21 ТЗ). Секретов в
// ответах нет: только параметры Windows и настройки SCU.

// 5) get_scu_context: снимок контекста приложения (не API-ключ).
internal sealed class ScuGetContextTool : IScuAiTool
{
    public string Name => "get_scu_context";

    public string Description =>
        "Текущий контекст SCU: версия, язык интерфейса, открытый раздел и выбранная " +
        "функция, права администратора, провайдер и модель AI, поддержка tools. " +
        "Используй, когда вопрос начинается с «что это делает» или «а это что».";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context) =>
        Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            application = context.Context.Application,
            version = context.Context.Version,
            language = context.Context.Language,
            current_section = context.Context.CurrentSectionNumber,
            current_section_title = context.Context.CurrentSectionTitle,
            current_utility_id = context.Context.CurrentUtilityId,
            current_utility_title = context.Context.CurrentUtilityTitle,
            is_admin = context.Context.IsAdmin,
            provider = context.Context.ProviderId,
            model = context.Context.Model,
            supports_tool_calling = context.Context.SupportsToolCalling,
        })));
}

// 6) get_system_summary: безопасный минимум о железе и ОС (без путей и секретов).
internal sealed class ScuGetSystemSummaryTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetSystemSummaryTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_system_summary";

    public string Description =>
        "Сводка о системе: Windows, процессор, память, видеокарты, диски. " +
        "Используй только когда вопрос касается железа/ОС, а не каждой задачи.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public async Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var info = _deps.State.Info;
        // Раздел мог не открываться: данные читаются один раз существующей
        // командой раздела, без нового обращения к WMI.
        if (info.SystemInfo is null)
        {
            await info.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        }

        if (info.SystemInfo is not { } system)
        {
            return ScuAiToolResult.Failure(
                ScuAiErrorCode.ExecutionFailed,
                L.T("Не удалось получить информацию о системе."));
        }

        return ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            os = system.OsCaption,
            os_version = system.OsVersion,
            cpu = system.CpuSummary,
            ram_mb = system.RamTotalBytes / 1024 / 1024,
            gpus = system.VideoAdapters,
            disks = system.Disks.Select(disk => new
            {
                name = disk.VolumeLabel,
                letter = disk.Letter,
                total_gb = Math.Round(disk.TotalBytes / 1024.0 / 1024 / 1024, 1),
                free_gb = Math.Round(disk.FreeBytes / 1024.0 / 1024 / 1024, 1),
            }),
        }));
    }
}

// 7) get_network_state: TCP-настройки и адаптеры через NetworkViewModel.
internal sealed class ScuGetNetworkStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetNetworkStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_network_state";

    public string Description =>
        "Состояние сети: автонастройка TCP, ECN, QoS override, NetBIOS, игровые " +
        "профили и список адаптеров с MTU.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var net = _deps.State.Network;
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            tcp_autotuning = net.SelectedAutoTuning,
            ecn = net.SelectedEcn,
            qos_override = net.QosOverrideValue,
            netbios = net.ActiveNetBiosKey,
            gaming_profile_active = net.IsGamingActive,
            mincifra_cert_installed = net.IsMinCifraCertInstalled,
            adapters = net.Interfaces.Select(adapter => new
            {
                name = adapter.Alias,
                mtu = adapter.Mtu,
            }),
            is_admin = net.IsAdmin,
        })));
    }
}

// 8) get_power_state: план питания и системные настройки через PowerViewModel.
internal sealed class ScuGetPowerStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetPowerStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_power_state";

    public string Description =>
        "Состояние питания: активный план, гибернация, быстрый запуск, сжатие памяти, " +
        "SysMain, короткие имена, доступ по дате, префетчер.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var power = _deps.State.Power;
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            active_plan = power.ActivePlanKey,
            hibernation = power.HibernationEnabled,
            fast_boot = power.FastBootEnabled,
            memory_compression = power.MemoryCompressionEnabled,
            sysmain = power.SysMainEnabled,
            short_names = power.ShortNamesEnabled,
            last_access = power.LastAccessEnabled,
            prefetcher = power.PrefetcherEnabled,
            is_admin = power.IsAdmin,
            interactive = power.IsInteractive,
        })));
    }
}

// 9) get_privacy_state: категории защиты приватности через PrivacyViewModel.
internal sealed class ScuGetPrivacyStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetPrivacyStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_privacy_state";

    public string Description =>
        "Состояние защиты приватности: какие категории (телеметрия, UWP-фоны, советы " +
        "и т.д.) защищены, а какие отключены.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var privacy = _deps.State.Privacy;
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            categories = privacy.Rows.Select(row => new
            {
                id = row.Category.Id,
                title = row.Category.Title,
                protected_ = row.IsEnabled,
            }),
            is_admin = privacy.IsAdmin,
            interactive = privacy.IsInteractive,
        })));
    }
}

// 10) get_ui_state: тумблеры проводника и визуальных эффектов через UIViewModel.
internal sealed class ScuGetUiStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetUiStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_ui_state";

    public string Description =>
        "Состояние интерфейса: переключатели проводника и визуальных эффектов " +
        "(анимации, прозрачность, эскизы, тени, контекстное меню и др.).";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var ui = _deps.State.Ui;
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            explorer = ui.ExplorerRows.Select(row => new { id = row.Id, title = row.LocalizedTitle, on = row.IsOn }),
            visual_fx = ui.VisualFxRows.Select(row => new { id = row.Id, title = row.LocalizedTitle, on = row.IsOn }),
            recommended = new { on = ui.RecommendedRow.IsOn },
            is_admin = ui.IsAdmin,
            interactive = ui.IsInteractive,
        })));
    }
}

// 11) get_input_state: мышь, клавиатура, игровые функции через InputViewModel.
internal sealed class ScuGetInputStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetInputStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_input_state";

    public string Description =>
        "Состояние ввода и игр: ускорение мыши, залипание клавиш, Game Bar, DVR, " +
        "Game Mode, ускоренный запуск Edge.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var input = _deps.State.Input;
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            switches = input.Rows.Select(row => new { id = row.Id, title = row.LocalizedTitle, on = row.IsOn }),
            is_admin = input.IsAdmin,
            interactive = input.IsInteractive,
        })));
    }
}

// 12) get_services_state: сводка раздела «Службы» — агрегированное состояние,
// без полного списка (п. 8C: «только релевантный агрегированный state»).
internal sealed class ScuGetServicesStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetServicesStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_services_state";

    public string Description =>
        "Сводка раздела «Службы»: сколько служб в списке SCU, сколько из них " +
        "включено, есть ли резервная копия и права администратора. " +
        "Полный список не возвращается — это сводка.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var services = _deps.State.Services;
        var enabled = services.Rows.Count(row => row.IsServiceEnabled);
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            loaded = services.Rows.Count > 0,
            services_total = services.Rows.Count,
            services_enabled = enabled,
            services_disabled = services.Rows.Count - enabled,
            has_backup = services.HasBackup,
            is_admin = services.IsAdmin,
            interactive = !services.IsBusy && services.IsAdmin,
        })));
    }
}

// 13) get_startup_state: сводка раздела «Автозагрузка» — элементы и резерв.
internal sealed class ScuGetStartupStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetStartupStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_startup_state";

    public string Description =>
        "Сводка раздела «Автозагрузка»: сколько элементов автозагрузки найдено, " +
        "есть ли резервный манифест для восстановления, права администратора.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var startup = _deps.State.Startup;
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            loaded = startup.Rows.Count > 0,
            items = startup.Rows.Count,
            has_restore_manifest = startup.HasRestoreManifest,
            is_admin = startup.IsAdmin,
            interactive = !startup.IsBusy && startup.IsAdmin,
        })));
    }
}

// 14) get_update_state: блокировка/пауза обновлений и драйверы через UpdateViewModel.
internal sealed class ScuGetUpdateStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetUpdateStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_update_state";

    public string Description =>
        "Состояние обновлений Windows: заблокированы ли службы обновлений, " +
        "активна ли пауза (и до какого срока), исключена ли установка драйверов.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var update = _deps.State.Update;
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            updates_blocked = update.IsWuBlocked,
            updates_paused = update.IsWuPaused,
            pause_info = string.IsNullOrWhiteSpace(update.PauseInfoText) ? null : update.PauseInfoText,
            driver_updates_excluded = update.DriverUpdatesExcluded,
            is_admin = update.IsAdmin,
            interactive = update.IsInteractive,
        })));
    }
}

// 15) get_maintenance_state: CompactOS, индексация дисков и точки восстановления.
internal sealed class ScuGetMaintenanceStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetMaintenanceStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_maintenance_state";

    public string Description =>
        "Сводка раздела «Поиск и целостность»: включён ли CompactOS, у каких " +
        "дисков отключена индексация, сколько есть точек восстановления.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var maintenance = _deps.State.Maintenance;
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            compact_os_enabled = maintenance.CompactOsEnabled,
            drives = maintenance.Drives.Select(drive => new
            {
                drive = drive.Display,
                indexing_disabled = drive.IsIndexingDisabled,
            }),
            restore_points = maintenance.RestorePoints.Count,
            integrity_running = maintenance.IsIntegrityRunning,
            is_admin = maintenance.IsAdmin,
            interactive = maintenance.IsInteractive,
        })));
    }
}

// 16) get_troubleshooting_state: результаты диагностических проб уже выполненного
// сканирования (повторный забег не запускается — см. ScuRunDiagnosticsTool).
internal sealed class ScuGetTroubleshootingStateTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetTroubleshootingStateTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_troubleshooting_state";

    public string Description =>
        "Результаты последней диагностики неполадок: проблемы, предупреждения и " +
        "найденные причины. Если сканирования не было — сообщает об этом.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var ts = _deps.State.Troubleshooting;
        if (!ts.HasRun)
        {
            return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
            {
                has_run = false,
                hint = "Диагностика ещё не выполнялась — предложи запустить run_scu_diagnostics.",
            })));
        }

        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            has_run = true,
            problems = ts.ProblemsCount,
            warnings = ts.WarningsCount,
            is_clean = ts.IsClean,
            findings = ts.Findings.Select(finding => new
            {
                title = finding.Title,
                description = finding.Description,
                severity = finding.Severity.ToString(),
            }),
        })));
    }
}
