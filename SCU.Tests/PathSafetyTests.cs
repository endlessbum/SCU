using SCU.Common;
using SCU.Services;
using Xunit;

namespace SCU.Tests;

// П. 5 аудита: граница пути без prefix-ошибок (StartsWith пропускает соседние
// каталоги: C:\Users\Alex2 начинается с C:\Users\Alex).
public class PathSafetyTests
{
    [Fact]
    public void ChildInsideParent_IsAccepted()
    {
        Assert.True(PathSafety.IsUnderDirectory(@"C:\Users\Alex\Temp\a.txt", @"C:\Users\Alex"));
        Assert.True(PathSafety.IsUnderDirectoryOrEqual(@"C:\Users\Alex\Temp\a.txt", @"C:\Users\Alex"));
    }

    [Fact]
    public void SiblingDirectoryWithPrefixName_IsRejected()
    {
        Assert.False(PathSafety.IsUnderDirectory(@"C:\Users\Alex2\a.txt", @"C:\Users\Alex"));
        Assert.False(PathSafety.IsUnderDirectoryOrEqual(@"C:\Users\Alex2\a.txt", @"C:\Users\Alex"));
    }

    [Fact]
    public void SiblingDirectoryWithoutSeparator_IsRejected()
    {
        Assert.False(PathSafety.IsUnderDirectory(@"C:\Users\AlexBackup\a.txt", @"C:\Users\Alex"));
    }

    [Fact]
    public void CaseInsensitivity_IsHandled()
    {
        Assert.True(PathSafety.IsUnderDirectory(@"c:\users\ALEX\Temp\a.txt", @"C:\Users\Alex"));
    }

    [Fact]
    public void ParentItself_NotUnder_StrictButEqualForOrEqual()
    {
        Assert.False(PathSafety.IsUnderDirectory(@"C:\Users\Alex", @"C:\Users\Alex"));
        Assert.True(PathSafety.IsUnderDirectoryOrEqual(@"C:\Users\Alex", @"C:\Users\Alex"));
    }

    [Fact]
    public void ParentDirectory_IsRejected()
    {
        Assert.False(PathSafety.IsUnderDirectory(@"C:\Users", @"C:\Users\Alex"));
    }

    [Fact]
    public void TraversalIsNormalizedBeforeDecision()
    {
        // ..\ нормализуется GetFullPath: итог — соседний каталог, не внутри.
        Assert.False(PathSafety.IsUnderDirectory(@"C:\Users\Alex\..\Alex2\x.txt", @"C:\Users\Alex"));
        Assert.True(PathSafety.IsUnderDirectory(@"C:\Users\Alex\.\Temp\x.txt", @"C:\Users\Alex"));
    }

    [Fact]
    public void OtherDrive_IsRejected()
    {
        Assert.False(PathSafety.IsUnderDirectory(@"D:\Users\Alex\a.txt", @"C:\Users\Alex"));
    }

    [Fact]
    public void EmptyOrNull_IsRejected_FailClosed()
    {
        Assert.False(PathSafety.IsUnderDirectory(null, @"C:\Users\Alex"));
        Assert.False(PathSafety.IsUnderDirectory(@"C:\x", null));
        Assert.False(PathSafety.IsUnderDirectoryOrEqual("", @"C:\Users\Alex"));
    }

    [Fact]
    public void FileCleanupService_TempDirectorySibling_IsRejected()
    {
        // Сосед профиля пользователя (префиксная коллизия) не проходит границу.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var siblingTemp = profile.TrimEnd('\\') + "2\\Temp";
        Assert.False(FileCleanupService.IsLikelyTempDirectory(siblingTemp));
        Assert.True(FileCleanupService.IsLikelyTempDirectory(Path.Combine(profile, "Temp")));
    }

    [Fact]
    public void InstalledAppsService_UninstallOwnershipBoundary()
    {
        // Извлечённая из private-API логика (internal): каталог процесса с тем
        // же префиксом, но другой папкой — не наш; соседняя папка не владеет.
        Assert.True(InstalledAppsService.IsPathUnderDirectoryForTest(@"C:\Program Files\App\App.exe", @"C:\Program Files\App"));
        Assert.False(InstalledAppsService.IsPathUnderDirectoryForTest(@"C:\Program Files\App2\App.exe", @"C:\Program Files\App"));
        Assert.False(InstalledAppsService.IsPathUnderDirectoryForTest(@"C:\Program Files\AppHelper\App.exe", @"C:\Program Files\App"));
        Assert.False(InstalledAppsService.IsPathUnderDirectoryForTest(null, @"C:\Program Files\App"));
    }
    // П. 6 аудита: ownership папки установки — деинсталлятор из uninstall
    // метаданных обязан лежать внутри корня; иначе папку не трогаем.
    [Fact]
    public void InstalledAppsService_Ownership_RequiresUninstallerInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "scu-own-" + Guid.NewGuid().ToString("N"));
        var inside = Path.Combine(root, "unins000.exe");
        Assert.True(InstalledAppsService.OwnsDirectoryForTest(inside, root));

        // Деинсталлятор в соседнем каталоге с префиксным именем — не владелец.
        var siblingRoot = root + "2";
        Assert.False(InstalledAppsService.OwnsDirectoryForTest(
            Path.Combine(siblingRoot, "unins000.exe"), root));
        Assert.False(InstalledAppsService.OwnsDirectoryForTest(
            Path.Combine(Path.GetTempPath(), "unins000.exe"), root));
    }

    // П. 6 аудита: папки данных по имени — только непосредственно в корне
    // AppData-каталога, не глубже и не в соседних корнях.
    [Fact]
    public void InstalledAppsService_DataFolder_MustBeDirectChildOfKnownRoot()
    {
        var baseRoot = Path.Combine(Path.GetTempPath(), "scu-data-" + Guid.NewGuid().ToString("N"));
        var siblingRoot = baseRoot + "-other";
        Assert.True(InstalledAppsService.IsDirectChildOfKnownRoot(
            Path.Combine(baseRoot, "MyApp"), [baseRoot]));
        Assert.False(InstalledAppsService.IsDirectChildOfKnownRoot(
            Path.Combine(baseRoot, "MyApp", "Sub"), [baseRoot]));
        Assert.False(InstalledAppsService.IsDirectChildOfKnownRoot(
            Path.Combine(siblingRoot, "MyApp"), [baseRoot]));
    }
}

