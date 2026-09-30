using SCU.Models;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

public class SystemSettingStateTests
{
    [Theory]
    [InlineData(SystemSettingState.Enabled, "")]
    [InlineData(SystemSettingState.Disabled, "")]
    [InlineData(SystemSettingState.Unknown, "?")]
    [InlineData(SystemSettingState.Unavailable, "—")]
    [InlineData(SystemSettingState.PendingReboot, "*")]
    [InlineData(SystemSettingState.Mixed, "≠")]
    [InlineData(SystemSettingState.PartiallyEnabled, "≠")]
    public void FormatStateBadge_MapsExpectedSymbols(SystemSettingState state, string expected)
    {
        Assert.Equal(expected, PowerViewModel.FormatStateBadge(state));
    }

    [Theory]
    [InlineData(SystemSettingState.Enabled, true)]
    [InlineData(SystemSettingState.Disabled, true)]
    [InlineData(SystemSettingState.PendingReboot, true)]
    [InlineData(SystemSettingState.Unknown, false)]
    [InlineData(SystemSettingState.Unavailable, false)]
    [InlineData(SystemSettingState.Mixed, false)]
    [InlineData(SystemSettingState.PartiallyEnabled, false)]
    public void IsToggleableState_OnlyBinaryOrPending(SystemSettingState state, bool expected)
    {
        Assert.Equal(expected, PowerViewModel.IsToggleableState(state));
    }

    [Theory]
    [InlineData(0, ShortNameGlobalMode.EnabledForAll, SystemSettingState.Enabled)]
    [InlineData(1, ShortNameGlobalMode.DisabledForAll, SystemSettingState.Disabled)]
    [InlineData(2, ShortNameGlobalMode.PerVolume, SystemSettingState.Mixed)]
    [InlineData(3, ShortNameGlobalMode.DisabledExceptSystem, SystemSettingState.PartiallyEnabled)]
    [InlineData(99, ShortNameGlobalMode.Unknown, SystemSettingState.Unknown)]
    public void ShortNameModes_MapCorrectly(int raw, ShortNameGlobalMode mode, SystemSettingState effective)
    {
        Assert.Equal(mode, PowerService.MapShortNameGlobalMode(raw));
        Assert.Equal(effective, PowerService.MapShortNameEffective(mode));
    }

    [Fact]
    public void OperationResult_PendingReboot_IsSuccess()
    {
        var r = OperationResult.Ok("written", requiresReboot: true);
        Assert.True(r.IsSuccess);
        Assert.True(r.RequiresReboot);
        Assert.Equal(OperationOutcome.PendingReboot, r.Outcome);
    }

    [Fact]
    public void OperationResult_AccessDenied_IsNotSuccess()
    {
        var r = OperationResult.AccessDenied();
        Assert.False(r.IsSuccess);
        Assert.Equal(OperationOutcome.AccessDenied, r.Outcome);
    }

    [Fact]
    public void CrashDumpInfo_CompleteAndKernel_MayRequirePagefile()
    {
        // Логика в ReadCrashDumpInfo: 1 и 2 требуют pagefile.
        // Проверяем модель, не реестр.
        var complete = new CrashDumpInfo(1, @"C:\Windows\MEMORY.DMP", true, "полный");
        var kernel = new CrashDumpInfo(2, null, true, "ядра");
        var none = new CrashDumpInfo(0, null, false, "нет");
        Assert.True(complete.MayRequirePagefile);
        Assert.True(kernel.MayRequirePagefile);
        Assert.False(none.MayRequirePagefile);
    }

    [Fact]
    public void PageFileInfo_SystemManaged_HasNoCustomSizesRequired()
    {
        var info = new PageFileInfo(
            SystemManaged: true,
            Entries: [new PageFileEntry(@"C:\pagefile.sys", null, null)],
            CrashDumpMayRequirePagefile: false,
            CrashDumpSummary: null);
        Assert.True(info.SystemManaged);
        Assert.Single(info.Entries);
    }

    [Fact]
    public void FastStartupInfo_Unavailable_WhenNoHiberfileButHiberbootOn()
    {
        var info = new FastStartupInfo(
            SystemSettingState.Unavailable,
            HiberbootEnabled: 1,
            HiberfileType.None,
            HybridSleepAvailable: false,
            HibernateAvailable: false,
            RequiresReboot: false);
        Assert.Equal(SystemSettingState.Unavailable, info.State);
        Assert.Equal(HiberfileType.None, info.HiberfileType);
    }

    [Fact]
    public void FastStartupInfo_PendingReboot_WhenRegistryAndHiberfileDisagree()
    {
        var pending = new FastStartupInfo(
            SystemSettingState.PendingReboot,
            HiberbootEnabled: 0,
            HiberfileType.Reduced,
            HybridSleepAvailable: true,
            HibernateAvailable: true,
            RequiresReboot: true);
        Assert.Equal(SystemSettingState.PendingReboot, pending.State);
        Assert.True(pending.RequiresReboot);
        Assert.Equal("*", PowerViewModel.FormatStateBadge(pending.State));
    }

    [Fact]
    public void LastAccessInfo_PendingReboot_BadgeIsStar()
    {
        var info = new LastAccessInfo(SystemSettingState.PendingReboot, RawValue: 1, RequiresReboot: true);
        Assert.True(info.RequiresReboot);
        Assert.Equal("*", PowerViewModel.FormatStateBadge(info.State));
    }

    [Fact]
    public void LastAccessInfo_Unknown_NotTreatedAsDisabled()
    {
        var info = new LastAccessInfo(SystemSettingState.Unknown, null, RequiresReboot: false);
        Assert.NotEqual(SystemSettingState.Disabled, info.State);
        Assert.Equal("?", PowerViewModel.FormatStateBadge(info.State));
    }

    [Fact]
    public void WindowsServiceInfo_AccessDenied_NotMissing()
    {
        var info = new WindowsServiceInfo(
            "SysMain", "SysMain",
            ServiceStartupType.Unknown,
            ServiceRuntimeStatus.Unknown,
            false,
            false,
            ServiceQueryStatus.AccessDenied);
        Assert.Equal(ServiceQueryStatus.AccessDenied, info.QueryStatus);
        Assert.Equal(WindowsServiceState.AccessDenied, info.LegacyState);
        Assert.NotEqual(WindowsServiceState.Missing, info.LegacyState);
    }

    [Fact]
    public void WindowsServiceInfo_DisabledRunning_IsStartAllowedFalse()
    {
        var info = new WindowsServiceInfo(
            "SysMain", "SysMain",
            ServiceStartupType.Disabled,
            ServiceRuntimeStatus.Running,
            false,
            IsStartAllowed: false,
            ServiceQueryStatus.Ok);
        Assert.False(info.IsStartAllowed);
        Assert.Equal(ServiceRuntimeStatus.Running, info.RuntimeStatus);
        Assert.Equal(WindowsServiceState.Disabled, info.LegacyState);
    }
}
