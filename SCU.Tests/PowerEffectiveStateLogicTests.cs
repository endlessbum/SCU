using SCU.Models;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

/// <summary>
/// Логика effective state без вызова Windows API (граничные случаи из §32 промпта).
/// </summary>
public class PowerEffectiveStateLogicTests
{
    [Fact]
    public void ShortName_Mode2_IsMixed_NotToggleable()
    {
        Assert.Equal(SystemSettingState.Mixed, PowerServiceMap(2));
        Assert.False(PowerViewModel.IsToggleableState(SystemSettingState.Mixed));
    }

    [Fact]
    public void ShortName_Mode0_IsEnabled_Toggleable()
    {
        Assert.Equal(SystemSettingState.Enabled, PowerServiceMap(0));
        Assert.True(PowerViewModel.IsToggleableState(SystemSettingState.Enabled));
    }

    [Fact]
    public void ShortName_Mode1_IsDisabled_Toggleable()
    {
        Assert.Equal(SystemSettingState.Disabled, PowerServiceMap(1));
        Assert.True(PowerViewModel.IsToggleableState(SystemSettingState.Disabled));
    }

    [Fact]
    public void ShortName_Mode3_IsPartiallyEnabled_NotToggleable()
    {
        Assert.Equal(SystemSettingState.PartiallyEnabled, PowerServiceMap(3));
        Assert.False(PowerViewModel.IsToggleableState(SystemSettingState.PartiallyEnabled));
    }

    [Fact]
    public void FastStartup_PendingReboot_IsToggleable()
    {
        // Пользователь может снова переключить, пока ждёт reboot.
        Assert.True(PowerViewModel.IsToggleableState(SystemSettingState.PendingReboot));
        Assert.Equal("*", PowerViewModel.FormatStateBadge(SystemSettingState.PendingReboot));
    }

    [Fact]
    public void FastStartup_Unavailable_IsNotToggleable()
    {
        Assert.False(PowerViewModel.IsToggleableState(SystemSettingState.Unavailable));
        Assert.Equal("—", PowerViewModel.FormatStateBadge(SystemSettingState.Unavailable));
    }

    [Fact]
    public void Unknown_NeverMapsToDisabledBadge()
    {
        Assert.Equal("?", PowerViewModel.FormatStateBadge(SystemSettingState.Unknown));
        Assert.NotEqual(PowerViewModel.FormatStateBadge(SystemSettingState.Disabled),
            PowerViewModel.FormatStateBadge(SystemSettingState.Unknown));
    }

    [Fact]
    public void PageFileInfo_MultipleEntries_PreservesPaths()
    {
        var info = new PageFileInfo(
            SystemManaged: false,
            Entries:
            [
                new PageFileEntry(@"C:\pagefile.sys", 4096, 4096),
                new PageFileEntry(@"D:\pagefile.sys", 2048, 2048)
            ],
            CrashDumpMayRequirePagefile: true,
            CrashDumpSummary: "Kernel dump");
        Assert.Equal(2, info.Entries.Count);
        Assert.True(info.CrashDumpMayRequirePagefile);
        Assert.Contains(info.Entries, e => e.Path.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OperationResult_FailedVerification_IsNotSuccess()
    {
        var r = OperationResult.Fail("verify failed", OperationOutcome.Failed);
        Assert.False(r.IsSuccess);
        Assert.Equal(OperationOutcome.Failed, r.Outcome);
    }

    private static SystemSettingState PowerServiceMap(int raw) =>
        SCU.Infrastructure.Windows.Power.PowerService.MapShortNameEffective(
            SCU.Infrastructure.Windows.Power.PowerService.MapShortNameGlobalMode(raw));
}
