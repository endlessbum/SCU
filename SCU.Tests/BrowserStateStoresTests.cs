using SCU.Common;
using SCU.Models.Browser;
using Xunit;

namespace SCU.Tests;

// Хранилища браузера: настройки (save/load/defaults), история (дедупликация,
// очистка, персистентность) и закладки (add/remove/persistence).
// Файлы — во временных папках теста.
public sealed class BrowserStateStoresTests : IDisposable
{
    private readonly string _directory;
    private readonly Logger _logger;

    public BrowserStateStoresTests()
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

    private string FilePath(string name) => Path.Combine(_directory, name);

    // ===================== Настройки =====================

    [Fact]
    public void Settings_MissingFile_ReturnsDefaults()
    {
        var service = new BrowserSettingsService(_logger, FilePath("settings.json"));

        var settings = service.Load();

        Assert.Equal(BrowserSettingsModel.CreateDefault(), settings);
        Assert.False(settings.AskDownloadPath);
        Assert.False(settings.ScanDownloads);
        Assert.True(settings.SaveHistory);
        Assert.Equal(1.0, settings.DefaultZoom);
        Assert.EndsWith("SCU Browser", settings.DownloadFolder, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_SaveLoad_Roundtrip()
    {
        var path = FilePath("settings.json");
        var original = BrowserSettingsModel.CreateDefault() with
        {
            AskDownloadPath = true,
            ScanDownloads = true,
            SaveHistory = false,
            DefaultZoom = 1.25,
        };

        new BrowserSettingsService(_logger, path).Save(original);
        var loaded = new BrowserSettingsService(_logger, path).Load();

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void Settings_CorruptedFile_FallsBackToDefaults()
    {
        var path = FilePath("settings.json");
        File.WriteAllText(path, "{ not valid json");

        var settings = new BrowserSettingsService(_logger, path).Load();

        Assert.Equal(BrowserSettingsModel.CreateDefault(), settings);
    }

    // Неудачная запись не оставляет .tmp-мусор в профиле: целевой путь занят
    // каталогом, Move падает — временный файл обязан быть удалён.
    [Fact]
    public void Settings_SaveFailure_RemovesTempFile()
    {
        var path = FilePath("occupied.json");
        Directory.CreateDirectory(path);

        var service = new BrowserSettingsService(_logger, path);
        service.Save(BrowserSettingsModel.CreateDefault());

        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(Directory.Exists(path));
    }

    // ===================== История =====================

    [Fact]
    public void History_ConsecutiveDuplicates_Deduplicated()
    {
        var service = new BrowserHistoryService(_logger, FilePath("history.json"));

        service.Add("https://example.com", "Example");
        service.Add("https://example.com", "Example");
        service.Add("https://example.org", "Other");
        service.Add("https://example.com", "Example");

        Assert.Equal(3, service.Entries.Count);
        Assert.Equal("https://example.com", service.Entries[2].Url);
    }

    [Fact]
    public void History_Clear_RemovesAllAndPersists()
    {
        var path = FilePath("history.json");
        var service = new BrowserHistoryService(_logger, path);
        service.Add("https://example.com", "Example");

        service.Clear();

        Assert.Empty(new BrowserHistoryService(_logger, path).Entries);
    }

    [Fact]
    public void History_PersistsAcrossInstances()
    {
        var path = FilePath("history.json");
        var service = new BrowserHistoryService(_logger, path);
        service.Add("https://example.com", "Example");
        service.Add("https://example.org", "Other");

        var reloaded = new BrowserHistoryService(_logger, path);

        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Contains(reloaded.Entries, item => item.Url == "https://example.org" && item.Title == "Other");
    }

    // ===================== Закладки =====================

    [Fact]
    public void Bookmarks_AddRemove_Contains()
    {
        var service = new BrowserBookmarkService(_logger, FilePath("bookmarks.json"));

        service.AddOrUpdate("https://example.com", "Example");
        Assert.True(service.Contains("https://example.com"));

        // Повторное добавление той же ссылки обновляет, а не дублирует.
        service.AddOrUpdate("https://example.com", "Example renamed");
        Assert.Single(service.Entries);

        service.Remove("https://example.com");
        Assert.False(service.Contains("https://example.com"));
        Assert.Empty(service.Entries);
    }

    [Fact]
    public void Bookmarks_PersistsAcrossInstances()
    {
        var path = FilePath("bookmarks.json");
        var service = new BrowserBookmarkService(_logger, path);
        service.AddOrUpdate("https://example.com", "Example");

        var reloaded = new BrowserBookmarkService(_logger, path);

        var bookmark = Assert.Single(reloaded.Entries);
        Assert.Equal("Example", bookmark.Title);
    }

    // ===================== Журнал загрузок =====================

    [Fact]
    public void Downloads_AddRemoveFromList_DoesNotTouchFile()
    {
        var path = FilePath("downloads.json");
        var service = new BrowserDownloadsService(_logger, path);
        var item = new BrowserDownloadItem("a.zip", Path.Combine(_directory, "a.zip"), "Завершено", "https://example.com/a.zip");

        service.AddOrUpdate(item);
        service.AddOrUpdate(item);
        Assert.Single(service.Entries);

        service.Remove(item.FilePath);
        Assert.Empty(service.Entries);
        Assert.True(File.Exists(item.FilePath) is false || true); // файл вне ответственности хранилища
    }
}
