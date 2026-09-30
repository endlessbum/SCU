using SCU.Models.Scan;
using SCU.Common;
using Xunit;

namespace SCU.Tests;

// Карантин (документ п. 21): изоляция без удаления по умолчанию, метаданные,
// восстановление, безвозвратное удаление. Все операции — на временных каталогах.
public class QuarantineServiceTests : IDisposable
{
    private readonly string _root;
    private readonly QuarantineService _service;

    public QuarantineServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scu-quarantine-test-" + Guid.NewGuid().ToString("N"));
        _service = new QuarantineService(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateSourceFile(string name, string content = "payload")
    {
        var path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static DetectionDto DetectionFor(string path) => new()
    {
        Path = path,
        Sha256 = "abc123",
        Verdict = "malware",
        RuleId = "HASH-DB",
        Description = "EICAR-Test-File",
    };

    [Fact]
    public void Quarantine_MovesOriginalToObjectsAndWritesMetadata()
    {
        var source = CreateSourceFile("bad.txt");

        Assert.True(_service.Quarantine(DetectionFor(source), out var error), error);
        Assert.False(File.Exists(source), "оригинал удаляется после фиксации копии");

        var items = _service.List();
        var item = Assert.Single(items);
        Assert.Equal(source, item.OriginalPath);
        Assert.Equal("malware", item.Verdict);
        Assert.True(File.Exists(Path.Combine(_root, "objects", item.Id + ".qtn")));
    }

    [Fact]
    public void Quarantine_MissingFile_FailsWithoutChanges()
    {
        Assert.False(_service.Quarantine(DetectionFor(
            Path.Combine(_root, "missing.txt")), out var error));
        Assert.NotEmpty(error);
        Assert.Empty(_service.List());
    }

    [Fact]
    public void Quarantine_ArchiveMember_UsesContainer()
    {
        // Член архива изолируется контейнером целиком (документ п. 20).
        var container = CreateSourceFile("outer.zip", "zip bytes");
        var detection = DetectionFor(container + @"\eicar.com");
        detection.ContainerPath = container;
        detection.IsVirtual = true;

        Assert.True(_service.Quarantine(detection, out var error), error);
        Assert.False(File.Exists(container));
        Assert.Equal(container, Assert.Single(_service.List()).OriginalPath);
    }

    [Fact]
    public void Restore_PutsFileBack_AndRemovesItem()
    {
        var source = CreateSourceFile("bad.txt");
        Assert.True(_service.Quarantine(DetectionFor(source), out _));
        var item = Assert.Single(_service.List());

        Assert.True(_service.Restore(item, out var error), error);
        Assert.True(File.Exists(source));
        Assert.Empty(_service.List());
    }

    [Fact]
    public void Restore_IntoOccupiedPlace_UsesRestoredSuffix()
    {
        var source = CreateSourceFile("bad.txt");
        Assert.True(_service.Quarantine(DetectionFor(source), out _));
        File.WriteAllText(source, "new occupant");
        var item = Assert.Single(_service.List());

        Assert.True(_service.Restore(item, out var error), error);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(_root, "src", "bad (restored).txt")));
        Assert.Empty(_service.List());
    }

    [Fact]
    public void Restore_TamperedObject_FailsIntegrityCheck()
    {
        var source = CreateSourceFile("bad.txt");
        Assert.True(_service.Quarantine(DetectionFor(source), out _));
        var item = Assert.Single(_service.List());

        // Объект изолирован, метаданные хранят хэш копии: подмена объекта должна
        // блокировать восстановление, а не разворачивать изменённый файл.
        var objectPath = Path.Combine(_root, "objects", item.Id + ".qtn");
        File.WriteAllText(objectPath, "tampered payload");

        Assert.False(_service.Restore(item, out var error));
        Assert.Contains("Хэш", error);
        Assert.Single(_service.List());
    }

    [Fact]
    public void Restore_RelativeOriginalPath_Fails()
    {
        var source = CreateSourceFile("bad.txt");
        Assert.True(_service.Quarantine(DetectionFor(source), out _));
        var item = Assert.Single(_service.List());

        item.OriginalPath = "relative\\bad.txt";
        Assert.False(_service.Restore(item, out var error));
        Assert.Contains("путь", error);
    }

    [Fact]
    public void DeletePermanently_RemovesObjectAndMetadata()
    {
        var source = CreateSourceFile("bad.txt");
        Assert.True(_service.Quarantine(DetectionFor(source), out _));
        var item = Assert.Single(_service.List());

        Assert.True(_service.DeletePermanently(item, out var error), error);
        Assert.Empty(_service.List());
        Assert.False(File.Exists(source));
    }
}
