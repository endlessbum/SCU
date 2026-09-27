using SCU.Services;
using Xunit;

namespace SCU.Tests;

// Классификация важности приложений для прочерка в «Приложениях».
public sealed class AppImportanceTests
{
    private static InstalledApp App(string name, string? publisher = null,
        string? location = null, string? uninstall = "MsiExec.exe /X{1}") =>
        new() { DisplayName = name, Publisher = publisher, InstallLocation = location, UninstallString = uninstall };

    [Theory]
    [InlineData("Microsoft Edge", "Microsoft Corporation")]
    [InlineData("Microsoft Edge WebView2 Runtime", "Microsoft Corporation")]
    [InlineData("Visual C++ Redistributable 2019", "Microsoft Corporation")]
    [InlineData("Обновление для Windows (KB5034123)", null)]
    public void Classify_CriticalSystemComponents(string name, string? publisher)
    {
        var entry = App(name, publisher);

        Assert.Equal(AppImportance.Critical, InstalledAppsService.Classify(entry, systemComponent: false, noRemove: false, isPerUser: false));
    }

    [Theory]
    [InlineData("Microsoft OneDrive", "Microsoft Corporation")]
    [InlineData("Visual Studio Code", "Microsoft")]
    public void Classify_MicrosoftAppsAreSystem(string name, string? publisher)
    {
        var entry = App(name, publisher);

        Assert.Equal(AppImportance.System, InstalledAppsService.Classify(entry, systemComponent: false, noRemove: false, isPerUser: false));
    }

    [Theory]
    [InlineData("Steam", "Valve Corporation")]
    [InlineData("Epic Games Launcher", "Epic Games Inc.")]
    [InlineData("Мой любимый лаунчер", "Independent Developer")]
    public void Classify_GamesAndLaunchersAreUser(string name, string? publisher)
    {
        var entry = App(name, publisher, location: @"C:\Games\X");

        Assert.Equal(AppImportance.User, InstalledAppsService.Classify(entry, systemComponent: false, noRemove: false, isPerUser: false));
    }

    [Fact]
    public void Classify_PerUserInstall_IsUser() =>
        Assert.Equal(AppImportance.User, InstalledAppsService.Classify(
            App("Мой блокнот", "Независимый разработчик"), systemComponent: false, noRemove: false, isPerUser: true));

    [Fact]
    public void Classify_NoRemove_IsCritical() =>
        Assert.Equal(AppImportance.Critical, InstalledAppsService.Classify(
            App("Встроенный компонент", "Microsoft Corporation"), systemComponent: false, noRemove: true, isPerUser: false));

    [Fact]
    public void Classify_SystemComponentFlag_IsCritical() =>
        Assert.Equal(AppImportance.Critical, InstalledAppsService.Classify(
            App("Служебный пакет", "Microsoft Corporation"), systemComponent: true, noRemove: false, isPerUser: false));

    [Fact]
    public void Classify_GameFromMicrosoftStore_IsUser()
    {
        var entry = App("Minecraft Launcher", "Microsoft Studios",
            location: @"C:\Program Files\WindowsApps\Microsoft.429714E0EC52A");

        Assert.Equal(AppImportance.User, InstalledAppsService.Classify(entry, systemComponent: false, noRemove: false, isPerUser: false));
    }
}
