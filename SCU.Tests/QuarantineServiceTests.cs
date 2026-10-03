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

    // ===================== Валидация Id (аудит п. 4) =====================

    // Id из внешних метаданных не превращается в путь напрямую: не-GUID-значения
    // (пути, разделители, ..) обязаны отклоняться до Path.Combine.
    [Theory]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("C:\\x")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")] // GUID "D" — с разделителями
    [InlineData("0123456789abcdef0123456789abcdef.qtn")] // GUID с расширением
    [InlineData("0123456789abcdef0123456789abcdeX")] // не hex
    [InlineData("0123456789abcdef0123456789abcde")] // короткий
    public void Restore_InvalidId_FailsWithoutPathEscape(string id)
    {
        var item = new QuarantineItem { Id = id, OriginalPath = "C:\\Windows\\notepad.exe" };
        Assert.False(_service.Restore(item, out var error));
        Assert.Contains("идентификатор", error);
        Assert.False(File.Exists("C:\\x.qtn"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("C:\\x")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("")]
    public void DeletePermanently_InvalidId_FailsWithoutDeletion(string id)
    {
        var item = new QuarantineItem { Id = id, OriginalPath = "C:\\Windows\\notepad.exe" };
        Assert.False(_service.DeletePermanently(item, out var error));
        Assert.Contains("идентификатор", error);
    }

    // Валидный "N"-GUID продолжает работать: объект открывается по ожидаемому пути.
    [Fact]
    public void GetObjectPath_ValidGuidN_ReturnsInsideObjects()
    {
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(Path.Combine(_root, "objects", id + ".qtn"), _service.GetObjectPath(id));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdeX")]
    public void GetObjectPath_InvalidId_ReturnsEmpty(string id)
    {
        Assert.Equal(string.Empty, _service.GetObjectPath(id));
    }

    // Целостность после TOCTOU-снимка (аудит п. 8): хэш объекта в метаданных
    // совпадает с хэшем исходника, который был на момент изоляции.
    [Fact]
    public void Quarantine_RecordsObjectHash_EqualToSourceContent()
    {
        var source = CreateSourceFile("bad.txt", "stable payload");
        Assert.True(_service.Quarantine(DetectionFor(source), out var error), error);
        var item = Assert.Single(_service.List());

        var objectPath = Path.Combine(_root, "objects", item.Id + ".qtn");
        Assert.Equal(item.SizeBytes, new FileInfo(objectPath).Length);
        Assert.Equal(item.ObjectSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(objectPath))),
            ignoreCase: true);
        Assert.False(File.Exists(source));
    }

    // ===================== Легаси-метаданные и битые записи (аудит 2, п. 26/29) =====================

    [Fact]
    public void Restore_LegacyMetadataWithoutHash_MigratesHashAndRestores()
    {
        // П. 26 аудита 2: легаси-запись без ObjectSha256 больше не
        // восстанавливается вслепую — хэш объекта фиксируется в метаданных.
        var source = CreateSourceFile("bad.txt");
        Assert.True(_service.Quarantine(DetectionFor(source), out var error), error);
        var item = Assert.Single(_service.List());

        // Эмулируем легаси-метаданные: вычищаем objectSha256.
        var metadataPath = Path.Combine(_root, "metadata", item.Id + ".json");
        var json = File.ReadAllText(metadataPath);
        File.WriteAllText(metadataPath, System.Text.RegularExpressions.Regex.Replace(
            json, "\"objectSha256\":\\s*\"[0-9a-fA-F]+\"", "\"objectSha256\": \"\""));

        var reloaded = Assert.Single(_service.List());
        Assert.Equal(string.Empty, reloaded.ObjectSha256);

        Assert.True(_service.Restore(reloaded, out error), error);
        Assert.True(File.Exists(source));
        Assert.Empty(_service.List());

        // Миграция записана: свежий хэш теперь в метаданных (файл уже удалён
        // успешным restore — проверяем через повторную изоляцию того же файла).
        Assert.True(_service.Quarantine(DetectionFor(source), out error), error);
        Assert.NotEqual(string.Empty, Assert.Single(_service.List()).ObjectSha256);
    }

    [Fact]
    public void List_CorruptedMetadata_SkippedButCounted()
    {
        // П. 29 аудита 2: битые метаданные пропускаются, но количество
        // всплывает пользователю, а не растворяется в «пустом» списке.
        var source = CreateSourceFile("bad.txt");
        Assert.True(_service.Quarantine(DetectionFor(source), out _));

        File.WriteAllText(Path.Combine(_root, "metadata", "broken.json"), "{ not json");

        var items = _service.List();
        Assert.Single(items);
        Assert.Equal(1, _service.CorruptedMetadataCount);
    }

    [Fact]
    public void List_CleanMetadata_ZeroCorrupted()
    {
        _service.List();
        Assert.Equal(0, _service.CorruptedMetadataCount);
    }
}
