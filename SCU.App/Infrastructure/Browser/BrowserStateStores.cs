using System.Text.Json;
using SCU.Common;
using SCU.Models.Browser;

namespace SCU.Infrastructure.Browser;

// Каталог данных браузера: %AppData%\SCU\browser (отдельно от истории операций SCU).
public static class BrowserDataPaths
{
    public static string Directory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU", "browser");

    public static string SettingsFile => Path.Combine(Directory, "settings.json");
    public static string HistoryFile => Path.Combine(Directory, "history.json");
    public static string BookmarksFile => Path.Combine(Directory, "bookmarks.json");
    public static string DownloadsFile => Path.Combine(Directory, "downloads.json");

    // Профиль WebView2 (UserDataFolder) — ТОЛЬКО для SCU Browser.
    public static string UserDataFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SCU", "Browser");
}

// Базовая запись/чтение JSON: та же стратегия (temp + move, best-effort чтение),
// что у остальных хранилищ SCU. Ошибки не роняют вызывающий код.
public abstract class BrowserJsonStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    protected readonly Logger Logger;
    protected readonly string Path;

    protected BrowserJsonStore(Logger logger, string path)
    {
        Logger = logger;
        Path = path;
    }

    protected T Load<T>(T fallback)
    {
        try
        {
            if (!File.Exists(Path))
            {
                return fallback;
            }

            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<T>(json) ?? fallback;
        }
        catch (Exception exception)
        {
            Logger.Warn("BROWSER | load failed | " + System.IO.Path.GetFileName(Path) + " | " + exception.Message);
            return fallback;
        }
    }

    protected void Save<T>(T value)
    {
        var temp = Path + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception exception)
        {
            // Остаток .tmp после неудачи — мусор в профиле и ложный «незавершённый
            // файл» при следующем чтении: убираем, ошибка не критична для данных.
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // Папка могла исчезнуть вместе с самим .tmp.
            }

            Logger.Warn("BROWSER | save failed | " + System.IO.Path.GetFileName(Path) + " | " + exception.Message);
        }
    }
}

// Настройки браузера.
public sealed class BrowserSettingsService : BrowserJsonStore
{
    public BrowserSettingsService(Logger logger, string? filePath = null)
        : base(logger, filePath ?? BrowserDataPaths.SettingsFile)
    {
    }

    public BrowserSettingsModel Load() =>
        Load(BrowserSettingsModel.CreateDefault());

    // base.: иначе Save(settings) разрешается в саму себя — бесконечная рекурсия.
    public void Save(BrowserSettingsModel settings) => base.Save(settings);
}

// История посещений: только top-level http/https навигации, без ресурсов страницы.
public sealed class BrowserHistoryService : BrowserJsonStore
{
    private const int MaxEntries = 2000;

    private List<BrowserHistoryItem> _entries;

    public BrowserHistoryService(Logger logger, string? filePath = null)
        : base(logger, filePath ?? BrowserDataPaths.HistoryFile)
    {
        _entries = Load<List<BrowserHistoryItem>>([]) ?? [];
    }

    public IReadOnlyList<BrowserHistoryItem> Entries => _entries;

    // Дедупликация: подряд идущий визит той же страницы не пишется второй раз.
    public void Add(string url, string title)
    {
        if (_entries.Count > 0
            && string.Equals(_entries[^1].Url, url, StringComparison.Ordinal))
        {
            return;
        }

        _entries.Add(new BrowserHistoryItem(url, title, DateTime.Now));
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }

        Save(_entries);
    }

    public void Clear()
    {
        _entries = [];
        Save(_entries);
    }
}

// Закладки.
public sealed class BrowserBookmarkService : BrowserJsonStore
{
    private List<BrowserBookmark> _entries;

    public BrowserBookmarkService(Logger logger, string? filePath = null)
        : base(logger, filePath ?? BrowserDataPaths.BookmarksFile)
    {
        _entries = Load<List<BrowserBookmark>>([]) ?? [];
    }

    public IReadOnlyList<BrowserBookmark> Entries => _entries;

    public bool Contains(string url) =>
        _entries.Any(bookmark => string.Equals(bookmark.Url, url, StringComparison.Ordinal));

    public void AddOrUpdate(string url, string title)
    {
        _entries.RemoveAll(bookmark => string.Equals(bookmark.Url, url, StringComparison.Ordinal));
        _entries.Add(new BrowserBookmark(url, title, DateTime.Now));
        Save(_entries);
    }

    public void Remove(string url)
    {
        _entries.RemoveAll(bookmark => string.Equals(bookmark.Url, url, StringComparison.Ordinal));
        Save(_entries);
    }
}

// Журнал загрузок: сам файл при «удалить из списка» не удаляется.
public sealed class BrowserDownloadsService : BrowserJsonStore
{
    private List<BrowserDownloadRecord> _entries;

    public BrowserDownloadsService(Logger logger, string? filePath = null)
        : base(logger, filePath ?? BrowserDataPaths.DownloadsFile)
    {
        _entries = Load<List<BrowserDownloadRecord>>([]) ?? [];
    }

    public IReadOnlyList<BrowserDownloadRecord> Entries => _entries;

    public void AddOrUpdate(BrowserDownloadItem item)
    {
        _entries.RemoveAll(record => string.Equals(record.FilePath, item.FilePath, StringComparison.Ordinal));
        _entries.Add(new BrowserDownloadRecord(
            item.FileName, item.FilePath, item.Url, item.StateKey, item.Timestamp));
        Save(_entries);
    }

    public void Remove(string filePath)
    {
        _entries.RemoveAll(record => string.Equals(record.FilePath, filePath, StringComparison.Ordinal));
        Save(_entries);
    }
}

// Сериализуемая запись журнала загрузок (BrowserDownloadItem — live-объект UI).
// StateKey — канонический ключ состояния, локализация только на слое UI.
public sealed record BrowserDownloadRecord(
    string FileName, string FilePath, string Url, string StateKey, DateTime Timestamp);
