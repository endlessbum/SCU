using SCU.Common;
using SCU.Infrastructure.Windows.Maintenance;
using Xunit;

namespace SCU.Tests;

// Механизмы защиты из security-аудита: резолв финального пути (junction/symlink)
// и отказ очистки temp-каталогов через подброшенный reparse point.
public sealed class SecurityHardeningTests : IDisposable
{
    private readonly string _directory;
    private readonly Logger _logger;

    public SecurityHardeningTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logger = Logger.CreateForCurrentRun();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична.
        }
    }

    // ===================== PathSafety.TryResolveFinalPath =====================

    [Fact]
    public void ResolveFinalPath_RegularDirectory_ReturnsCanonicalPath()
    {
        var target = Path.Combine(_directory, "plain");
        Directory.CreateDirectory(target);

        var ok = PathSafety.TryResolveFinalPath(target, out var resolved);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(target).TrimEnd('\\', '/'), resolved.TrimEnd('\\', '/'));
    }

    [Fact]
    public void ResolveFinalPath_MissingPath_FailsClosed()
    {
        var missing = Path.Combine(_directory, "no-such-dir", "deeper");

        Assert.False(PathSafety.TryResolveFinalPath(missing, out var resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void ResolveFinalPath_Junction_RevealsTarget()
    {
        // Junction (в отличие от symlink) создаётся без привилегий.
        var real = Path.Combine(_directory, "real");
        Directory.CreateDirectory(real);
        var junction = Path.Combine(_directory, "link");
        CreateJunction(junction, real);

        var ok = PathSafety.TryResolveFinalPath(junction, out var resolved);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(real).TrimEnd('\\', '/'), resolved.TrimEnd('\\', '/'));
    }

    // ===================== Очистка temp-каталогов =====================

    // Подброшенный в очищаемый каталог junction не должен уводить удаление
    // наружу: жертва за junction обязана выжить.
    [Fact]
    public async Task Cleanup_JunctionInsideTarget_DoesNotTouchOutsideFiles()
    {
        var victimDir = Path.Combine(_directory, "victim");
        Directory.CreateDirectory(victimDir);
        var victimFile = Path.Combine(victimDir, "keep.txt");
        File.WriteAllText(victimFile, "do not delete");

        var target = Path.Combine(_directory, "target");
        var nested = Path.Combine(target, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "clean-me.txt"), "delete me");
        CreateJunction(Path.Combine(nested, "link"), victimDir);

        var service = new FileCleanupService(_logger);
        var result = await service.CleanAsync(
            [new CleanupTarget("Test", target, "*.txt", RemoveSubdirectories: false)],
            progress: null);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(victimFile), "Файл за junction удалён — граница обхода нарушена");
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ArgumentList = { "/d", "/c", "mklink", "/J", junctionPath, targetPath },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit(10_000);
        Assert.True(Directory.Exists(junctionPath), "Не удалось создать junction для теста");
    }
}
