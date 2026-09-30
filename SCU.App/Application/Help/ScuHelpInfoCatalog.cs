namespace SCU.AppCore.Help;

// Каталог текстов ⓘ-информеров (I_*) с привязкой к разделу и утилите: каждый
// подробный текст справки ведёт к реальной функции SCU. Заполняется один раз
// при сборке индекса; ключи берутся из Themes/Strings.*.xaml.
internal static class ScuHelpInfoCatalog
{
    // (ключ I_*, раздел, связанная утилита — null, если текст описывает раздел/функцию целиком).
    internal static readonly (string InfoKey, int Section, string? UtilityId)[] Entries =
    [
        ("I_CleanupRecycleBin", 3, "clean_recyclebin"),
        ("I_CleanupTemp", 3, "clean_temp"),
        ("I_CleanupBrowsers", 3, "clean_browsers"),
        ("I_CleanupUpdateCache", 3, "clean_update_cache"),
        ("I_MaintDoCache", 12, "maint_do_cache"),
        ("I_MaintDumpsWer", 12, "maint_dumps_wer"),
        ("I_NetAdapterProfileApply", 9, "net_adapter_apply"),
        ("I_NetApplyAutoTuning", 9, "net_autotuning_normal"),
        ("I_NetApplyEcn", 9, "net_ecn_default"),
        ("I_NetGamingProfile", 9, "net_gaming_apply"),
        ("I_NetMtu", 9, null),
        ("I_NetMtuCustom", 9, null),
        ("I_NetMtuRestore", 9, "net_mtu_restore"),
        ("I_NetNetBiosFlush", 9, "net_netbios_flush"),
        ("I_NetNetBiosMode", 9, null),
        ("I_NetQosRemove", 9, "net_qos_remove"),
        ("I_NetQosRestore", 9, "net_qos_restore"),
        ("I_NetQosSet", 9, null),
        ("I_NetResetAll", 9, "net_reset_all"),
        ("I_PlanBalanced", 8, null),
        ("I_PlanBitsum", 8, null),
        ("I_PlanHigh", 8, null),
        ("I_PlanPowerSaver", 8, null),
        ("I_PlanUltimate", 8, null),
        ("I_PowerCpuClear", 8, "power_cpu_clear"),
        ("I_PowerPageFile", 8, null),
        ("I_PrivacyEnableAll", 5, null),
        ("I_RunAsAdmin", 0, null),
        ("I_SvcBackup", 6, "svc_backup"),
        ("I_SvcRestore", 6, "svc_restore"),
        ("I_TasksBackup", 15, "tasks_backup"),
        ("I_TasksDisableAll", 15, null),
        ("I_TasksRestore", 15, "tasks_restore"),
        ("I_UiClearTaskbar", 10, "ui_clear_taskbar"),
        ("I_UiMenuDelay", 10, null),
        ("I_UiRestartExplorer", 10, "ui_restart_explorer"),
        ("I_UiRestoreTaskbar", 10, "ui_restore_taskbar"),
    ];
}
