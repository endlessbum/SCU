using SCU.Common;
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

    // П. 6 аудита: широкие корни (вплоть до корня диска) не удаляются как
    // «папка установки», даже если ownership формально подтверждён —
    // InstallLocation в реестре бывает ошибочен, а деинсталлятор лежит внутри.
    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\Program Files\Common Files")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\")]
    public void InstalledAppsService_SharedRoots_AreProtected(string directory)
    {
        Assert.True(InstalledAppsService.IsProtectedSharedDirectoryForTest(directory));
    }

    // Обычная папка приложения-потомка и временные каталоги защитой не считаются.
    [Theory]
    [InlineData(@"C:\Program Files\MyApp")]
    [InlineData(@"C:\Program Files (x86)\MyApp")]
    [InlineData(@"D:\Apps\MyApp")]
    public void InstalledAppsService_PerAppFolders_AreNotSharedRoots(string directory)
    {
        Assert.False(InstalledAppsService.IsProtectedSharedDirectoryForTest(directory));
    }

    // Ownership сам по себе дыру не закрывает: деинсталлятор внутри C:\Program
    // Files делает ownership «подтверждённым» — от удаления широкий корень спасает
    // именно защита общих каталогов (обе проверки работают вместе).
    [Fact]
    public void InstalledAppsService_OwnershipAlone_DoesNotProtectSharedRoot()
    {
        Assert.True(InstalledAppsService.OwnsDirectoryForTest(
            @"C:\Program Files\unins000.exe", @"C:\Program Files"));
        Assert.True(InstalledAppsService.IsProtectedSharedDirectoryForTest(@"C:\Program Files"));
    }

    [Theory]
    [InlineData(@"C:\Users\Alex\AppData\Local\Microsoft")]
    [InlineData(@"C:\ProgramData\Microsoft")]
    [InlineData(@"C:\Users\Alex\AppData\Local\Temp")]
    [InlineData(@"C:\ProgramData\Common Files")]
    public void InstalledAppsService_ProtectedDataFolderNames_AreBlocked(string directory)
    {
        Assert.True(InstalledAppsService.IsProtectedDataFolderNameForTest(directory));
    }

    [Theory]
    [InlineData(@"C:\Users\Alex\AppData\Local\MyApp")]
    [InlineData(@"C:\ProgramData\MyApp Corp")]
    public void InstalledAppsService_PlainDataFolderNames_AreAllowed(string directory)
    {
        Assert.False(InstalledAppsService.IsProtectedDataFolderNameForTest(directory));
    }

    // ===================== П. 5 аудита: reparse points =====================

    // Junction создаётся без прав администратора; symlink — нет, поэтому
    // reparse-кейсы закрываются именно junction'ами (та же семантика границы).
    private static void RemoveTreeWithJunction(string root, params string[] junctions)
    {
        // Junction удаляется как пустая ссылка (без рекурсии в цель), затем
        // обычное дерево — рекурсивное удаление по junction падает.
        foreach (var junction in junctions)
        {
            try
            {
                Directory.Delete(junction, recursive: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        Directory.Delete(root, recursive: true);
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void JunctionInsideParent_PointingOutside_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "scu-reparse-" + Guid.NewGuid().ToString("N"));
        var safe = Path.Combine(root, "safe");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(safe);
        Directory.CreateDirectory(outside);
        try
        {
            var link = Path.Combine(safe, "link");
            CreateJunction(link, outside);

            var filePath = Path.Combine(outside, "leak.txt");
            File.WriteAllText(filePath, "data");

            // Файл физически вне safe; путь через junction обязан резолвиться в
            // конечный путь и не пройти проверку границы.
            Assert.False(PathSafety.IsUnderDirectory(Path.Combine(link, "leak.txt"), safe));
            Assert.False(InstalledAppsService.IsPathUnderDirectoryForTest(
                Path.Combine(link, "evil.exe"), safe));
        }
        finally
        {
            RemoveTreeWithJunction(root, Path.Combine(safe, "link"));
        }
    }

    [Fact]
    public void JunctionAsParent_ResolvesConsistently_ChildInsideIsAccepted()
    {
        var root = Path.Combine(Path.GetTempPath(), "scu-reparse-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(root, "junc");
        try
        {
            CreateJunction(link, real);

            var filePath = Path.Combine(real, "inside.txt");
            File.WriteAllText(filePath, "data");

            // Junction указывает внутрь: обе стороны нормализуются к одному
            // конечному пути, ребёнок через junction проходит границу.
            Assert.True(PathSafety.IsUnderDirectory(Path.Combine(link, "inside.txt"), link));
            Assert.True(PathSafety.IsUnderDirectory(Path.Combine(link, "inside.txt"), real));
            Assert.True(PathSafety.IsUnderDirectory(filePath, link));
        }
        finally
        {
            RemoveTreeWithJunction(root, link);
        }
    }

    [Fact]
    public void JunctionEscapingInstallRoot_UninstallProcessPath_IsRejected()
    {
        // Uninstall-сценарий (п. 5/6): junction внутри папки установки, ведущий к
        // чужому каталогу, не делает чужие процессы «процессами приложения».
        var root = Path.Combine(Path.GetTempPath(), "scu-reparse-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "App");
        var other = Path.Combine(root, "Other");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(other);
        try
        {
            CreateJunction(Path.Combine(app, "escape"), other);
            var victim = Path.Combine(other, "victim.exe");
            File.WriteAllText(victim, "not really an exe");

            Assert.False(InstalledAppsService.IsPathUnderDirectoryForTest(
                Path.Combine(app, "escape", "victim.exe"), app));
        }
        finally
        {
            RemoveTreeWithJunction(root, Path.Combine(app, "escape"));
        }
    }
}

