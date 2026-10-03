using SCU.Infrastructure.Storage;
using Xunit;

namespace SCU.Tests;

// PinnedCardsStore (pinned-cards.json): roundtrip списка закреплений с точным
// порядком и устойчивость к повреждённому файлу / недоступной папке.
public sealed class PinnedCardsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;
    private readonly Logger _logger;

    public PinnedCardsStoreTests()
    {
        // Уникальная временная папка на каждый тест; удаление — в Dispose.
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "pinned-cards.json");
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
            // Временная папка не критична: сбой удаления не роняет тест.
        }
    }

    private PinnedCardsStore CreateStore() => new(_logger, _filePath);

    [Fact]
    public void Save_Load_RoundtripPreservesIdsAndOrder()
    {
        // Порядок важен: область на «Главной» показывает карточки в порядке
        // закрепления, поэтому Store обязан сохранять список как есть.
        var pinned = new List<string>
        {
            "net.qos",
            "input.game-mode",
            "maint.winsxs",
            "settings.addscript",
        };

        CreateStore().Save(pinned);
        var loaded = CreateStore().Load();

        Assert.Equal(pinned, loaded);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList()
    {
        var loaded = CreateStore().Load();

        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsEmptyList()
    {
        File.WriteAllText(_filePath, "не json вовсе");

        var loaded = CreateStore().Load();

        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_CorruptedFile_CleansStaleTempFile()
    {
        // П. №15 аудита: остаток .tmp после сбоя убирается при чтении.
        File.WriteAllText(_filePath + ".tmp", "мусор");
        File.WriteAllText(_filePath, "{broken");

        CreateStore().Load();

        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void Save_UnwritableDirectory_DoesNotThrow()
    {
        var store = new PinnedCardsStore(_logger,
            Path.Combine(_directory, "no-such-dir", "sub", "pinned-cards.json"));

        var exception = Record.Exception(() => store.Save(["net.tcp"]));

        Assert.Null(exception);
    }

    [Fact]
    public void Save_OverwritesPreviousContent()
    {
        var store = CreateStore();
        store.Save(["net.tcp", "net.dns"]);
        store.Save(["maint.integrity"]);

        var loaded = store.Load();

        var single = Assert.Single(loaded);
        Assert.Equal("maint.integrity", single);
    }

    [Fact]
    public void Load_ObjectJson_ReturnsEmptyList()
    {
        // Валидный JSON, но не массив — десериализация списка обязана дать пусто.
        File.WriteAllText(_filePath, "{}");

        Assert.Empty(CreateStore().Load());
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyList()
    {
        File.WriteAllText(_filePath, string.Empty);

        Assert.Empty(CreateStore().Load());
    }

    [Fact]
    public void Save_EmptyList_PersistsEmptyFile()
    {
        CreateStore().Save([]);

        Assert.True(File.Exists(_filePath));
        Assert.Empty(CreateStore().Load());
    }

    [Fact]
    public void Save_Load_RoundtripWithSpecialCharacters()
    {
        // JSON обязан переживать кавычки, пробелы, табы и юникод в id.
        var pinned = new List<string> { "with space", "quote\"id", "юникод-тест", "tab\tid" };

        CreateStore().Save(pinned);

        Assert.Equal(pinned, CreateStore().Load());
    }
}
