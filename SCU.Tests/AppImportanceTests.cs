using Xunit;

namespace SCU.Tests;

// Классификация важности приложений для прочерка в «Приложениях».
// Правило: зелёный прочерк и активное «Удалить» — у всего, что не поставляется
// с официальной сборкой Windows (в т.ч. рекламные и навязанные корпорацией);
// серый прочерк и неактивная кнопка — только у компонентов самой Windows.
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
    // Навязанные корпорацией и рекламные приложения — не компоненты Windows:
    // зелёный прочерк и активное «Удалить».
    [InlineData("Microsoft OneDrive", "Microsoft Corporation")]
    [InlineData("Microsoft Teams", "Microsoft Corporation")]
    [InlineData("Microsoft 365 Office", "Microsoft Corporation")]
    [InlineData("Microsoft Visual Studio Code", "Microsoft")]
    [InlineData("Xbox Identity Provider", "Microsoft Corporation")]
    [InlineData("McAfee LiveSafe", "McAfee, Inc.")]
    public void Classify_CorporateBloat_IsUser(string name, string? publisher)
    {
        var entry = App(name, publisher);

        Assert.Equal(AppImportance.User, InstalledAppsService.Classify(entry, systemComponent: false, noRemove: false, isPerUser: false));
    }

    [Theory]
    [InlineData("NVIDIA Graphics Driver 551.23", "NVIDIA Corporation")]
    [InlineData("AMD Chipset Software", "Advanced Micro Devices")]
    [InlineData("Драйвер Realtek Audio", "Realtek")]
    public void Classify_HardwareDrivers_AreSystem(string name, string? publisher)
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
    public void Classify_GameFromMicrosoftStore_IsUser()
    {
        var entry = App("Minecraft Launcher", "Microsoft Studios",
            location: @"C:\Program Files\WindowsApps\Microsoft.429714E0EC52A");

        Assert.Equal(AppImportance.User, InstalledAppsService.Classify(entry, systemComponent: false, noRemove: false, isPerUser: false));
    }

    [Theory]
    [InlineData("Battle.net", "Blizzard Entertainment")]
    [InlineData("7-Zip 24.09 (x64)", "Igor Pavlov")]
    [InlineData("Служебный пакет", "Microsoft Corporation")]
    public void Classify_SystemComponentFlag_DoesNotRaiseImportance(string name, string? publisher)
    {
        // SystemComponent у сторонних и корпоративных записей — «скрыть из
        // классического списка», на важность для системы это не указывает.
        var entry = App(name, publisher, location: @"C:\Program Files\Battle.net");

        Assert.Equal(AppImportance.User, InstalledAppsService.Classify(entry, systemComponent: true, noRemove: false, isPerUser: false));
    }

    [Fact]
    public void Classify_MachineWideThirdPartyApp_IsUser() =>
        Assert.Equal(AppImportance.User, InstalledAppsService.Classify(
            App("Notepad++", "Notepad++ Team", location: @"C:\Program Files\Notepad++"),
            systemComponent: false, noRemove: false, isPerUser: false));

    [Theory]
    [InlineData(@"C:\Users\User\AppData\Local\Programs\MyApp")]
    [InlineData(@"%LocalAppData%\Programs\MyApp")]
    public void Classify_InstallLocationInUserProfile_IsUser(string location) =>
        Assert.Equal(AppImportance.User, InstalledAppsService.Classify(
            App("Сторонняя программа", "Сторонний издатель", location: location),
            systemComponent: false, noRemove: false, isPerUser: false));
}
