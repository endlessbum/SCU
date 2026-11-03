using Xunit;

namespace SCU.Tests;

// Инфраструктура тестов: TestScratch обязан выдавать уникальные пути под
// доступным для записи корнем (%TEMP%\SCU.Tests, иначе — рядом со сборкой).
// Падение этих тестов обесценило бы весь временно-файловый пул сьюты.
public class TestScratchTests
{
    [Fact]
    public void Root_IsReachableAndWritable()
    {
        var probe = Path.Combine(TestScratch.Root, $"probe_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "1");
        Assert.True(File.Exists(probe));
        File.Delete(probe);
    }

    [Fact]
    public void NewDir_ReturnsDistinctExistingDirectories()
    {
        var first = TestScratch.NewDir("tw-dir-");
        var second = TestScratch.NewDir("tw-dir-");

        try
        {
            Assert.StartsWith(TestScratch.Root, first, StringComparison.Ordinal);
            Assert.StartsWith(TestScratch.Root, second, StringComparison.Ordinal);
            Assert.NotEqual(first, second);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void NewDir_WithoutPrefix_StillUnderRoot()
    {
        var dir = TestScratch.NewDir();
        try
        {
            Assert.StartsWith(TestScratch.Root, dir, StringComparison.Ordinal);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NewFile_BuildsUniquePathWithoutCreatingFile()
    {
        var first = TestScratch.NewFile("tw_file", "txt");
        var second = TestScratch.NewFile("tw_file", ".bin");

        Assert.StartsWith(TestScratch.Root, first, StringComparison.Ordinal);
        Assert.StartsWith(TestScratch.Root, second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.EndsWith(".txt", first, StringComparison.Ordinal);
        Assert.EndsWith(".bin", second, StringComparison.Ordinal);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }
}
