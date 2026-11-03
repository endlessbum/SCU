using SCU.Common;
using Xunit;

namespace SCU.Tests;

public sealed class FileCleanupCancellationTests : IDisposable
{
    private readonly string _directory;
    private readonly FileCleanupService _service;

    public FileCleanupCancellationTests()
    {
        _directory = Path.Combine(AppContext.BaseDirectory, "testdata", "clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _service = new FileCleanupService(Logger.CreateForCurrentRun());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task CleanAsync_AlreadyCancelled_ReturnsFailureCancelled()
    {
        File.WriteAllText(Path.Combine(_directory, "a.tmp"), "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _service.CleanAsync(
            [new CleanupTarget("Test", _directory, "*.tmp")],
            progress: null,
            cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(-1, result.Code);
        Assert.Contains("Отменено", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(_directory, "a.tmp")));
    }

    [Fact]
    public void IsLikelyTempDirectory_NullOrEmpty_FailsClosed()
    {
        Assert.False(FileCleanupService.IsLikelyTempDirectory(null));
        Assert.False(FileCleanupService.IsLikelyTempDirectory(""));
        Assert.False(FileCleanupService.IsLikelyTempDirectory("   "));
    }

    [Fact]
    public void GetTempTargets_UserTempOutsideProfile_FallsBackToLocalAppDataTemp()
    {
        var original = Environment.GetEnvironmentVariable("TEMP");
        try
        {
            // Путь с листом Temp, но вне профиля — не «likely», берётся LocalAppData\Temp.
            Environment.SetEnvironmentVariable("TEMP", @"D:\NotAUserProfile\Temp");
            var targets = FileCleanupService.GetTempTargets();
            var userTemp = Assert.Single(targets, t => t.Label == "TEMP пользователя");
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            Assert.Equal(
                Path.GetFullPath(expected).TrimEnd('\\'),
                Path.GetFullPath(userTemp.Directory).TrimEnd('\\'));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", original);
        }
    }
}
