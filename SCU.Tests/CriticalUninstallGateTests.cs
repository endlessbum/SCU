using SCU.Common;
using Xunit;

namespace SCU.Tests;

// Critical-компоненты Windows не удаляются ни через StartUninstall, ни через
// UninstallCompletely — гейт на Importance, не только на UI CanUninstall.
public sealed class CriticalUninstallGateTests
{
    private static readonly Logger Logger = Logger.CreateForCurrentRun();

    private static InstalledApp App(AppImportance importance) => new()
    {
        DisplayName = "Microsoft Edge Update",
        UninstallString = @"C:\Windows\System32\cmd.exe /c exit 0",
        InstallLocation = Path.Combine(Path.GetTempPath(), "scu-crit-unused"),
        Importance = importance
    };

    [Fact]
    public void CanUninstall_Critical_IsFalse()
    {
        Assert.False(App(AppImportance.Critical).CanUninstall);
        Assert.True(App(AppImportance.User).CanUninstall);
        Assert.True(App(AppImportance.System).CanUninstall);
    }

    [Fact]
    public void StartUninstall_Critical_RejectedWithoutLaunch()
    {
        var service = new InstalledAppsService(Logger);
        var result = service.StartUninstall(App(AppImportance.Critical));

        Assert.False(result.IsSuccess);
        Assert.Contains("Windows", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UninstallCompletely_Critical_RejectedWithoutLaunch()
    {
        var service = new InstalledAppsService(Logger);
        var result = await service.UninstallCompletelyAsync(
            App(AppImportance.Critical), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Windows", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartUninstall_NoUninstaller_Rejected()
    {
        var service = new InstalledAppsService(Logger);
        var result = service.StartUninstall(new InstalledApp
        {
            DisplayName = "No Unins",
            Importance = AppImportance.User
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("нет программы удаления", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
