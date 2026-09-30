using SCU.Common;
using Xunit;

namespace SCU.Tests;

// Безопасные имена файлов загрузок: traversal, абсолютные пути, UNC,
// зарезервированные имена Windows, дубликаты — всё обязано нейтрализоваться.
public sealed class BrowserDownloadPathTests
{
    private static string TempDownloadFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SCU.Tests", "dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void BuildDownloadPath_TraversalName_StayInsideFolder()
    {
        var folder = TempDownloadFolder();
        var path = BrowserDownloadPolicy.BuildDownloadPath(folder, "..\\evil.exe");

        Assert.NotNull(path);
        Assert.True(PathSafety.IsUnderDirectory(path, folder));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void BuildDownloadPath_AbsolutePath_OnlyFileNameKept()
    {
        var folder = TempDownloadFolder();
        var path = BrowserDownloadPolicy.BuildDownloadPath(folder, "C:\\Windows\\System32\\evil.exe");

        Assert.NotNull(path);
        Assert.Equal("evil.exe", Path.GetFileName(path));
        Assert.True(PathSafety.IsUnderDirectory(path, folder));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void BuildDownloadPath_UncPath_OnlyFileNameKept()
    {
        var folder = TempDownloadFolder();
        var path = BrowserDownloadPolicy.BuildDownloadPath(folder, "\\\\server\\share\\evil.exe");

        Assert.NotNull(path);
        Assert.Equal("evil.exe", Path.GetFileName(path));
        Directory.Delete(folder, recursive: true);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT2")]
    public void BuildDownloadPath_ReservedNames_Prefixed(string reserved)
    {
        var folder = TempDownloadFolder();
        var path = BrowserDownloadPolicy.BuildDownloadPath(folder, reserved);

        Assert.NotNull(path);
        Assert.False(
            string.Equals(Path.GetFileNameWithoutExtension(path), reserved, StringComparison.OrdinalIgnoreCase));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void BuildDownloadPath_UrlEncodedTraversal_Neutralized()
    {
        var folder = TempDownloadFolder();
        var path = BrowserDownloadPolicy.BuildDownloadPath(folder, "..%2F..%2Fevil.exe");

        Assert.NotNull(path);
        Assert.True(PathSafety.IsUnderDirectory(path, folder));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void BuildDownloadPath_DuplicateName_GetsIndexSuffix()
    {
        var folder = TempDownloadFolder();
        var first = BrowserDownloadPolicy.BuildDownloadPath(folder, "report.pdf");
        File.WriteAllText(first!, "x");
        var second = BrowserDownloadPolicy.BuildDownloadPath(folder, "report.pdf");

        Assert.NotEqual(first, second);
        Assert.Equal("report (1).pdf", Path.GetFileName(second));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void BuildDownloadPath_EmptyName_ReturnsNull() =>
        Assert.Null(BrowserDownloadPolicy.BuildDownloadPath(TempDownloadFolder(), "  "));

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("installer.MSI", true)]
    [InlineData("run.ps1", true)]
    [InlineData("archive.zip", false)]
    [InlineData("document.pdf", false)]
    public void IsExecutable_DetectsDangerousExtensions(string name, bool expected) =>
        Assert.Equal(expected, BrowserDownloadPolicy.IsExecutable(name));
}
