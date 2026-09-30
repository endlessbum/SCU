using Xunit;
using SCU.Models;

namespace SCU.Tests;

// Правила диагностики — чистые функции от DiagnosticContext (п. 32 плана:
// главный риск — ложные срабатывания). Тесты фиксируют ключевую семантику:
// осознанные настройки пользователя не превращаются в ошибки, пороги соблюдаются.
public class DiagnosticRulesTests
{
    private static DiagnosticContext HealthyContext()
    {
        var context = new DiagnosticContext
        {
            OsCaption = "Windows 11 Pro",
            PendingReboot = false,
            Disks =
            [
                new DiagnosticDiskSnapshot("C:", 500L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, true, "NTFS")
            ],
            Services =
            [
                new ServiceSnapshot("wuauserv", "Windows Update", true, true),
                new ServiceSnapshot("BITS", "BITS", true, true),
                new ServiceSnapshot("WinDefend", "Defender", true, true)
            ],
            UpdatePaused = false,
            UpdateBlocked = false,
            NetworkHasActiveAdapter = true,
            NetworkHasGateway = true,
            NetworkDnsResolves = true,
            UacStandard = true,
            AntivirusRegistered = true,
            AntivirusRealTimeOn = true,
            EventErrors = [],
            StartupEntries = [],
            AudioDevices =
            [
                new AudioDeviceSnapshot("Realtek Audio", "Realtek", "OK", 0)
            ]
        };
        return context;
    }

    // ===================== Вторая очередь: драйверы, звук, печать =====================

    [Fact]
    public void ProblemDevices_AreWarningWhileManuallyDisabledAreInfo()
    {
        // Код 22 = отключено вручную (осознанная настройка), остальные — проблема.
        var context = HealthyContext();
        context.DriverProblems =
        [
            new DriverProblemSnapshot("Wi-Fi Adapter", "PCI\\VEN_1", "Intel", 28),
            new DriverProblemSnapshot("Old Camera", "USB\\VID_1", "Generic", 22)
        ];

        var findings = DiagnosticRules.Evaluate(context);

        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(findings, f => f.Id == "drivers.problem_devices").Severity);
        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(findings, f => f.Id == "drivers.disabled_devices").Severity);
        // Устройство с кодом 22 не попадает в проблемные.
        Assert.DoesNotContain(
            Assert.Single(findings, f => f.Id == "drivers.problem_devices").Evidence,
            evidence => evidence.Label.Contains("Old Camera"));
    }

    [Fact]
    public void StoppedAudioService_IsWarningWithStartFix()
    {
        var context = HealthyContext();
        context.Services =
        [
            new ServiceSnapshot("wuauserv", "Windows Update", true, true),
            new ServiceSnapshot("Audiosrv", "Windows Audio", true, false)
        ];

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "services.audio_stopped");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains(finding.Actions, action => action.FixId == "start_service");
    }

    [Fact]
    public void StoppedSpooler_IsInfoNotWarning()
    {
        // Отключение Spooler — частая осознанная настройка (п. 24 плана).
        var context = HealthyContext();
        context.Services =
        [
            new ServiceSnapshot("wuauserv", "Windows Update", true, true),
            new ServiceSnapshot("Spooler", "Print Spooler", true, false)
        ];

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "services.spooler_stopped");
        Assert.Equal(DiagnosticSeverity.Info, finding.Severity);
        // Без start-исправления: запуск печати — решение пользователя.
        Assert.DoesNotContain(finding.Actions, action => action.Kind == DiagnosticActionKind.RunFix);
    }

    // ===================== Вторая очередь: накопители, время =====================

    [Fact]
    public void SmartPredictedFailure_IsWarning()
    {
        var context = HealthyContext();
        context.SmartFailures = [new SmartFailureSnapshot("SamsungSSD_980")];

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "storage.smart_failure");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains(finding.Actions, action =>
            action.Kind == DiagnosticActionKind.OpenApp && action.AppTarget == "diskmgmt.msc");
    }

    [Fact]
    public void StoppedTimeService_IsInfo()
    {
        // w32time — trigger-start служба: остановка не равна поломке (п. 24 плана).
        var context = HealthyContext();
        context.Services =
        [
            new ServiceSnapshot("wuauserv", "Windows Update", true, true),
            new ServiceSnapshot("w32time", "Windows Time", true, false)
        ];

        var findings = DiagnosticRules.Evaluate(context);

        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(findings).Severity);
    }

    [Fact]
    public void DemandStartServices_Stopped_ProduceNoFindings()
    {
        // msiserver/bthserv — demand-start: остановка штатна, находок быть не должно.
        var context = HealthyContext();
        context.Services =
        [
            new ServiceSnapshot("wuauserv", "Windows Update", true, true),
            new ServiceSnapshot("msiserver", "Windows Installer", true, false),
            new ServiceSnapshot("bthserv", "Bluetooth", true, false)
        ];

        var findings = DiagnosticRules.Evaluate(context);
        Assert.DoesNotContain(findings, f => f.Category == DiagnosticCategory.Services);
    }

    // ===================== Вторая очередь: аудио =====================

    [Fact]
    public void StoppedAudioEndpointBuilder_IsWarningWithStartFix()
    {
        var context = HealthyContext();
        context.Services =
        [
            new ServiceSnapshot("wuauserv", "Windows Update", true, true),
            new ServiceSnapshot("AudioEndpointBuilder", "Audio Endpoint Builder", true, false)
        ];

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "services.audio_endpoint_stopped");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains(finding.Actions, action => action.FixId == "start_service");
    }

    [Fact]
    public void NoAudioDevices_IsInfo()
    {
        // Отсутствие устройств — не всегда поломка (HDMI/USB), поэтому Info.
        var context = HealthyContext();
        context.AudioDevices = [];

        var findings = DiagnosticRules.Evaluate(context);

        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(findings).Severity);
    }

    [Fact]
    public void SoundDeviceWithErrorStatus_IsWarning()
    {
        var context = HealthyContext();
        context.AudioDevices =
        [
            new AudioDeviceSnapshot("Realtek Audio", "Realtek", "Error", 0),
            new AudioDeviceSnapshot("NVIDIA HD Audio", "NVIDIA", "OK", 0)
        ];

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "audio.device_error");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.DoesNotContain(finding.Evidence, evidence => evidence.Label.Contains("NVIDIA"));
    }

    [Fact]
    public void HealthyAudioDevices_ProduceNoFindings()
    {
        var context = HealthyContext();
        context.AudioDevices =
        [
            new AudioDeviceSnapshot("Realtek Audio", "Realtek", "OK", 0)
        ];

        var findings = DiagnosticRules.Evaluate(context);
        Assert.DoesNotContain(findings, f => f.Category == DiagnosticCategory.Audio);
    }

    [Fact]
    public void HealthyContext_ProducesNoFindings()
    {
        var findings = DiagnosticRules.Evaluate(HealthyContext());
        Assert.Empty(findings);
    }

    // ===================== Диск =====================

    [Theory]
    [InlineData(6.0, DiagnosticSeverity.Warning)]
    [InlineData(4.0, DiagnosticSeverity.Critical)]
    public void LowSystemDiskSpace_ProducesFindingWithRightSeverity(double freeGb, DiagnosticSeverity expected)
    {
        var context = HealthyContext();
        context.Disks = [new DiagnosticDiskSnapshot(
            "C:", 500L * 1024 * 1024 * 1024, (ulong)(freeGb * 1024 * 1024 * 1024), true, "NTFS")];

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "disk.low_space");
        Assert.Equal(expected, finding.Severity);
        Assert.NotEmpty(finding.Actions);
    }

    [Fact]
    public void EnoughFreeSpace_NoLowSpaceFinding()
    {
        var findings = DiagnosticRules.Evaluate(HealthyContext());
        Assert.DoesNotContain(findings, f => f.Id == "disk.low_space");
    }

    // ===================== Windows Update =====================

    [Fact]
    public void UpdateServiceStopped_NotBlocked_IsWarningWithStartFix()
    {
        var context = HealthyContext();
        context.Services =
        [
            new ServiceSnapshot("wuauserv", "Windows Update", true, false)
        ];

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "update.service_stopped");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains(finding.Actions, action =>
            action.Kind == DiagnosticActionKind.RunFix && action.FixId == "start_service");
    }

    [Fact]
    public void UpdateServiceBlocked_IsInfoNotWarning()
    {
        // Отключение обновлений — осознанная настройка (п. 5/7.2 плана): не ошибка.
        var context = HealthyContext();
        context.Services =
        [
            new ServiceSnapshot("wuauserv", "Windows Update", false, false)
        ];
        context.UpdateBlocked = true;

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "update.blocked");
        Assert.Equal(DiagnosticSeverity.Info, finding.Severity);
        Assert.DoesNotContain(findings, f => f.Id == "update.service_stopped");
    }

    [Fact]
    public void UpdatePaused_IsInfoOnly()
    {
        var context = HealthyContext();
        context.UpdatePaused = true;

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings);
        Assert.Equal(DiagnosticSeverity.Info, finding.Severity);
    }

    // ===================== Сеть =====================

    [Fact]
    public void DnsFailure_ProducesWarningWithFlushDnsFix()
    {
        var context = HealthyContext();
        context.NetworkDnsResolves = false;

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "network.dns_failure");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains(finding.Actions, action => action.FixId == "flush_dns");
    }

    [Fact]
    public void NoActiveAdapter_IsCritical()
    {
        var context = HealthyContext();
        context.NetworkHasActiveAdapter = false;

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "network.no_adapter");
        Assert.Equal(DiagnosticSeverity.Critical, finding.Severity);
        // Остановка оценки: без адаптера DNS/шлюз не проверяются.
        Assert.DoesNotContain(findings, f => f.Category == DiagnosticCategory.Network && f.Id != "network.no_adapter");
    }

    // ===================== Службы =====================

    [Fact]
    public void AlwaysOnServiceStopped_IsWarningWithFix()
    {
        var context = HealthyContext();
        context.Services =
        [
            new ServiceSnapshot("wuauserv", "Windows Update", true, true),
            new ServiceSnapshot("Dhcp", "DHCP Client", true, false)
        ];

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "services.stopped.Dhcp");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
    }

    // ===================== События (дедупликация) =====================

    [Fact]
    public void ManyIdenticalCrashes_AggregateIntoSingleFinding()
    {
        // 50 одинаковых событий → одна проблема (п. 7.6 плана).
        var context = HealthyContext();
        context.EventErrors =
        [
            new EventErrorSummary("Application Error", 1000, "Application", 50, DateTime.Now.AddHours(-1))
        ];

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "events.app_crashes");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void FewCrashes_StayBelowThreshold_NoFinding()
    {
        var context = HealthyContext();
        context.EventErrors =
        [
            new EventErrorSummary("Application Error", 1000, "Application", 2, DateTime.Now.AddHours(-1))
        ];

        var findings = DiagnosticRules.Evaluate(context);
        Assert.DoesNotContain(findings, f => f.Id == "events.app_crashes");
    }

    [Fact]
    public void BugCheck_IsCriticalWhileKernelPower41IsWarning()
    {
        var context = HealthyContext();
        context.EventErrors =
        [
            new EventErrorSummary("Kernel-Power", 41, "System", 1, DateTime.Now.AddHours(-2))
        ];

        var findings = DiagnosticRules.Evaluate(context);
        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(findings).Severity);

        var contextWithBugCheck = HealthyContext();
        contextWithBugCheck.EventErrors =
        [
            new EventErrorSummary("BugCheck", 1001, "System", 1, DateTime.Now.AddHours(-2))
        ];

        var bugFindings = DiagnosticRules.Evaluate(contextWithBugCheck);
        var bugFinding = Assert.Single(bugFindings, f => f.Id == "events.unexpected_reboot");
        Assert.Equal(DiagnosticSeverity.Critical, bugFinding.Severity);
    }

    // ===================== Системные файлы =====================

    [Fact]
    public void ComponentStoreCorruption_ProducesConfirmedFix()
    {
        var context = HealthyContext();
        context.ComponentStoreCorrupted = true;

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "sysfiles.corruption");
        var fix = Assert.Single(finding.Actions);
        Assert.Equal(DiagnosticActionKind.RunFix, fix.Kind);
        Assert.True(fix.RequiresConfirm, "DISM RestoreHealth должен требовать подтверждения (п. 7.1/17 плана)");
        Assert.Contains("sysfiles", fix.VerifyProbeIds!);
    }

    // ===================== Автозагрузка =====================

    [Fact]
    public void InvalidStartupEntries_AreWarningAndDuplicatesAreInfo()
    {
        var context = HealthyContext();
        context.StartupEntries =
        [
            new StartupEntrySnapshot("Ghost", @"C:\Missing\app.exe", "HKCU Run", "missing"),
            new StartupEntrySnapshot("Dup", @"C:\Tools\run.exe", "HKLM Run", "duplicate")
        ];

        var findings = DiagnosticRules.Evaluate(context);

        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(findings, f => f.Id == "startup.invalid_entries").Severity);
        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(findings, f => f.Id == "startup.duplicates").Severity);
    }

    // ===================== Безопасность =====================

    [Fact]
    public void RealTimeProtectionOff_IsWarning()
    {
        var context = HealthyContext();
        context.AntivirusRealTimeOn = false;

        var findings = DiagnosticRules.Evaluate(context);

        var finding = Assert.Single(findings, f => f.Id == "security.realtime_off");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
    }

    // ===================== Система =====================

    [Fact]
    public void PendingReboot_IsInfo()
    {
        var context = HealthyContext();
        context.PendingReboot = true;

        var findings = DiagnosticRules.Evaluate(context);

        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(findings).Severity);
    }

    [Fact]
    public void EveryFinding_HasLocalizedTitleAndEvidence()
    {
        var context = HealthyContext();
        context.Disks = [new DiagnosticDiskSnapshot("C:", 500L * 1024 * 1024 * 1024, 3L * 1024 * 1024 * 1024, true, "NTFS")];
        context.NetworkDnsResolves = false;
        context.EventErrors = [new EventErrorSummary("WHEA-Logger", 17, "System", 4, null)];
        context.ComponentStoreCorrupted = true;
        context.PendingReboot = true;

        var findings = DiagnosticRules.Evaluate(context);
        Assert.NotEmpty(findings);
        Assert.All(findings, finding =>
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Title));
            Assert.False(string.IsNullOrWhiteSpace(finding.Description));
            Assert.All(finding.Evidence, evidence =>
            {
                Assert.False(string.IsNullOrWhiteSpace(evidence.Label));
                Assert.False(string.IsNullOrWhiteSpace(evidence.Value));
            });
        });
    }
}
