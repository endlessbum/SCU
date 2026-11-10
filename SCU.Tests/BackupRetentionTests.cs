using SCU.Common;
using Xunit;

namespace SCU.Tests;

// П.2: retention резервных копий — в каталоге хранятся только MaxFilesPerDirectory
// самых свежих файлов, старые удаляются при создании нового.
public sealed class BackupRetentionTests : IDisposable
{
    private readonly string _directory;

    public BackupRetentionTests()
    {
        _directory = TestScratch.NewDir();
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична: сбой удаления не роняет тест.
        }
    }

    private string CreateFile(string name, int ageMinutes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, name);
        var stamp = DateTime.UtcNow.AddMinutes(-ageMinutes);
        File.SetLastWriteTimeUtc(path, stamp);
        File.SetCreationTimeUtc(path, stamp);
        return path;
    }

    [Fact]
    public void Enforce_FewerThanLimit_DeletesNothing()
    {
        for (var i = 0; i < BackupRetention.MaxFilesPerDirectory; i++)
        {
            CreateFile($"tasks_{i:D2}.txt", i);
        }

        BackupRetention.Enforce(_directory);

        Assert.Equal(BackupRetention.MaxFilesPerDirectory, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void Enforce_MoreThanLimit_KeepsOnlyTenNewest()
    {
        var created = new List<string>();
        for (var i = 0; i < BackupRetention.MaxFilesPerDirectory + 5; i++)
        {
            // Возраст растёт с i: created[0] — самый свежий, created[^1] — самый старый.
            created.Add(CreateFile($"tasks_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{i:D2}.txt", i));
        }

        BackupRetention.Enforce(_directory);

        var remaining = Directory.GetFiles(_directory);
        Assert.Equal(BackupRetention.MaxFilesPerDirectory, remaining.Length);
        // Самый свежий файл остался.
        Assert.Contains(created[0], remaining);
        // Самые старые пять удалены.
        Assert.DoesNotContain(created[^1], remaining);
        Assert.DoesNotContain(created[^5], remaining);
    }

    [Fact]
    public void Enforce_MissingDirectory_DoesNotThrow()
    {
        var missing = Path.Combine(_directory, "not-created");

        BackupRetention.Enforce(missing);

        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void EnforceAll_WalksSubdirectories()
    {
        var nested = Path.Combine(_directory, "services");
        Directory.CreateDirectory(nested);
        for (var i = 0; i < BackupRetention.MaxFilesPerDirectory + 3; i++)
        {
            File.WriteAllText(Path.Combine(nested, $"services_{i:D2}.txt"), "x");
        }

        BackupRetention.EnforceAll(_directory);

        Assert.Equal(BackupRetention.MaxFilesPerDirectory, Directory.GetFiles(nested).Length);
    }

    // Аудит, п. 2: каталог вне backup-корня уборке не подлежит — иначе удаление
    // могло уйти по junction/symlink за пределы backup-дерева.
    [Fact]
    public void Enforce_DirectoryOutsideRoot_IsNotCleaned()
    {
        for (var i = 0; i < BackupRetention.MaxFilesPerDirectory + 3; i++)
        {
            CreateFile($"tasks_{i:D2}.txt", i);
        }

        var otherRoot = Path.Combine(Path.GetDirectoryName(_directory)!, "other-root");
        Directory.CreateDirectory(otherRoot);
        try
        {
            BackupRetention.Enforce(_directory, null, otherRoot);

            Assert.Equal(BackupRetention.MaxFilesPerDirectory + 3, Directory.GetFiles(_directory).Length);
        }
        finally
        {
            try
            {
                Directory.Delete(otherRoot, recursive: true);
            }
            catch
            {
                // Временный каталог не критичен.
            }
        }
    }

    // OBS-02 (аудит 2026-11-04): одноразовые снимки исходного состояния твиков
    // не удаляются по свежести, даже когда вытесняются накоплениями.
    [Fact]
    public void Enforce_ExemptSnapshotFiles_AreNeverDeleted()
    {
        // 15 накопительных файлов + 5 одноразовых снимков (самые старые).
        for (var i = 0; i < BackupRetention.MaxFilesPerDirectory + 5; i++)
        {
            CreateFile($"pagefile_backup_{i:D4}.json", i);
        }

        var exemptPaths = new List<string>();
        foreach (var name in BackupRetention.RetentionExemptFileNames)
        {
            exemptPaths.Add(CreateFile(name, 1000));
        }

        BackupRetention.Enforce(_directory);

        var remaining = Directory.GetFiles(_directory);
        Assert.Equal(BackupRetention.MaxFilesPerDirectory + exemptPaths.Count, remaining.Length);
        foreach (var path in exemptPaths)
        {
            Assert.Contains(path, remaining);
        }
    }

    // OBS-02: вложенные подкаталоги (backup\power\pagefile) обходятся рекурсивно.
    [Fact]
    public void EnforceAll_WalksNestedSubdirectories()
    {
        var power = Path.Combine(_directory, "power");
        var pagefile = Path.Combine(power, "pagefile");
        Directory.CreateDirectory(pagefile);
        for (var i = 0; i < BackupRetention.MaxFilesPerDirectory + 4; i++)
        {
            File.WriteAllText(Path.Combine(pagefile, $"pagefile_backup_{i:D4}.json"), "x");
        }

        BackupRetention.EnforceAll(_directory);

        Assert.Equal(BackupRetention.MaxFilesPerDirectory, Directory.GetFiles(pagefile).Length);
    }

    [Fact]
    public void Enforce_CustomLimit_KeepsOnlyRequestedCount()
    {
        for (var i = 0; i < BackupRetention.MaxFilesPerDirectory; i++)
        {
            CreateFile($"pagefile_backup_{i:D4}.json", i);
        }

        BackupRetention.Enforce(_directory, null, null, 5);

        Assert.Equal(5, Directory.GetFiles(_directory).Length);
    }
}
