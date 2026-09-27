using CommunityToolkit.Mvvm.ComponentModel;
using SCU.Common;

namespace SCU.Models.Browser;

// Модель вкладки встроенного браузера (раздел 22). Держит только то, что нужно
// UI; состояния WebView2 (история переходов) остаются в самом WebView2.
public sealed class BrowserTabModel : ObservableObject
{
    private string _title;
    private string _url;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private double _progress;
    private bool _isActive;
    private string _statusText = string.Empty;

    public BrowserTabModel(int id, string url, string title)
    {
        Id = id;
        _url = url;
        _title = title;
    }

    public int Id { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        set => SetProperty(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        set => SetProperty(ref _canGoForward, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    // Временное сообщение вкладки (ошибка сети, блокировка навигации, сбой рендера).
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }
}

// Запись истории браузера: отдельное хранилище, НЕ HistoryStore операций SCU.
public sealed record BrowserHistoryItem(string Url, string Title, DateTime Timestamp);

// Закладка.
public sealed record BrowserBookmark(string Url, string Title, DateTime Timestamp);

// Состояние одной загрузки.
public sealed record DownloadProgressInfo(long BytesReceived, long? TotalBytes);

public sealed class BrowserDownloadItem : ObservableObject
{
    // Канонический ключ состояния (не локализованный): пишется в downloads.json,
    // на экран отображается переводом — иначе смена языка даёт смешение строк.
    public const string StateDownloading = "Downloading";
    public const string StateCompleted = "Completed";
    public const string StateCanceled = "Canceled";
    public const string StateError = "Error";
    public const string StateChecking = "Checking";
    public const string StateThreat = "Threat";
    public const string StateAborted = "Aborted";

    private string _stateKey;
    private long _bytesReceived;
    private long? _totalBytes;
    private string? _scanResult;

    public BrowserDownloadItem(string fileName, string filePath, string stateKey, string url)
    {
        FileName = fileName;
        FilePath = filePath;
        _stateKey = stateKey;
        Url = url;
        Timestamp = DateTime.Now;
    }

    public string FileName { get; }

    public string FilePath { get; }

    public string Url { get; }

    public DateTime Timestamp { get; }

    public string StateKey
    {
        get => _stateKey;
        set
        {
            if (SetProperty(ref _stateKey, value))
            {
                OnPropertyChanged(nameof(State));
            }
        }
    }

    // Локализованное состояние для UI — производное от StateKey.
    public string State => L.T(StateKey switch
    {
        StateDownloading => "Идёт загрузка",
        StateCompleted => "Завершено",
        StateCanceled => "Отменено",
        StateError => "Ошибка загрузки",
        StateChecking => "Проверяется",
        StateThreat => "Угроза",
        StateAborted => "Прервано",
        _ => "Ошибка загрузки",
    });

    // Загрузки, прерванные извне (SCU убит посреди загрузки), не «висят» активными.
    public bool IsUnfinished => StateKey is StateDownloading or StateChecking;

    public long BytesReceived
    {
        get => _bytesReceived;
        set => SetProperty(ref _bytesReceived, value);
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        set => SetProperty(ref _totalBytes, value);
    }

    // Итог проверки ScannerCore (если включена): «чисто» / «N угроз» / null.
    public string? ScanResult
    {
        get => _scanResult;
        set
        {
            if (SetProperty(ref _scanResult, value))
            {
                OnPropertyChanged(nameof(HasScanResult));
            }
        }
    }

    public bool HasScanResult => !string.IsNullOrEmpty(_scanResult);
}

// Настройки браузера: лёгкий JSON, без БД. Часть полей пока только хранится
// (архитектурный задел: приватная сессия, восстановление сессии).
public sealed record BrowserSettingsModel(
    string DownloadFolder,
    bool AskDownloadPath,
    bool ScanDownloads,
    bool SaveHistory,
    bool ClearHistoryOnExit,
    bool ClearCookiesOnExit,
    bool ClearCacheOnExit,
    double DefaultZoom)
{
    public static BrowserSettingsModel CreateDefault() => new(
        DownloadFolder: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "SCU Browser"),
        AskDownloadPath: false,
        ScanDownloads: false,
        SaveHistory: true,
        ClearHistoryOnExit: false,
        ClearCookiesOnExit: false,
        ClearCacheOnExit: false,
        DefaultZoom: 1.0);
}
