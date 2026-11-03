using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCU.Models.DeepSeek;

namespace SCU.Infrastructure.DeepSeek;

// Каталог данных раздела DeepSeek: %AppData%\SCU\deepseek (по образцу
// BrowserDataPaths — отдельно от состояния браузера и общего state).
public static class DeepSeekDataPaths
{
    public static string Directory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU", "deepseek");

    public static string SettingsFile => Path.Combine(Directory, "settings.json");
    public static string ChatsFile => Path.Combine(Directory, "chats.json");
}

// Базовая запись/чтение JSON: та же стратегия (temp + move, best-effort чтение),
// что у BrowserJsonStore. Ошибки не роняют вызывающий код.
public abstract class DeepSeekJsonStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    protected readonly Logger Logger;
    protected readonly string Path;

    protected DeepSeekJsonStore(Logger logger, string path)
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
            Logger.Warn("DEEPSEEK | load failed | " + System.IO.Path.GetFileName(Path) + " | " + exception.Message);
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
            // Остаток .tmp после неудачи — мусор в профиле: убираем, ошибка не критична.
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // Папка могла исчезнуть вместе с самим .tmp.
            }

            Logger.Warn("DEEPSEEK | save failed | " + System.IO.Path.GetFileName(Path) + " | " + exception.Message);
        }
    }
}

// Настройки раздела: активный провайдер + ключ/модель на каждого
// (переключение провайдера не требует повторного ввода ключа).
public sealed class DeepSeekSettingsModel
{
    // Id активного провайдера: "deepseek" | "openrouter" (DeepSeekProviderConfig).
    public string ActiveProvider { get; set; } = "deepseek";

    public string DeepSeekApiKey { get; set; } = string.Empty;
    public string DeepSeekChatModel { get; set; } = string.Empty;
    public string OpenRouterApiKey { get; set; } = string.Empty;
    public string OpenRouterChatModel { get; set; } = string.Empty;

    // Cloudflare: кроме ключа нужен Account ID (входит в base URL запросов).
    public string CloudflareApiKey { get; set; } = string.Empty;
    public string CloudflareAccountId { get; set; } = string.Empty;
    public string CloudflareChatModel { get; set; } = string.Empty;

    // Поля старого формата (до появления провайдеров): читаются при загрузке
    // и мигрируют в слот DeepSeek, обратно не пишутся.
    public string ApiKey { get; set; } = string.Empty;
    public string ChatModel { get; set; } = string.Empty;

    public (string ApiKey, string ChatModel) GetActive() => ActiveProvider switch
    {
        "openrouter" => (OpenRouterApiKey, OpenRouterChatModel),
        "cloudflare" => (CloudflareApiKey, CloudflareChatModel),
        _ => (DeepSeekApiKey, DeepSeekChatModel),
    };

    public string GetActiveAccountId() => ActiveProvider == "cloudflare" ? CloudflareAccountId : string.Empty;

    public void SetActive(string apiKey, string chatModel) =>
        SetActive(apiKey, chatModel, GetActiveAccountId());

    public void SetActive(string apiKey, string chatModel, string accountId)
    {
        switch (ActiveProvider)
        {
            case "openrouter":
                OpenRouterApiKey = apiKey;
                OpenRouterChatModel = chatModel;
                break;
            case "cloudflare":
                CloudflareApiKey = apiKey;
                CloudflareChatModel = chatModel;
                CloudflareAccountId = accountId;
                break;
            default:
                DeepSeekApiKey = apiKey;
                DeepSeekChatModel = chatModel;
                break;
        }
    }
}

public sealed class DeepSeekSettingsService : DeepSeekJsonStore
{
    // Аудит: API-ключи хранились в settings.json открытым текстом. Шифруем DPAPI
    // (CurrentUser) — файл читается только под тем же пользователем Windows.
    // Маркер-префикс отличает шифротекст от значений старого формата.
    private const string DpapiPrefix = "dpapi:v1:";

    public DeepSeekSettingsService(Logger logger, string? filePath = null)
        : base(logger, filePath ?? DeepSeekDataPaths.SettingsFile)
    {
    }

    public DeepSeekSettingsModel Load()
    {
        var settings = Load(new DeepSeekSettingsModel());
        DecryptKeys(settings);
        // Миграция старого settings.json: единый ключ → слот DeepSeek.
        if (!string.IsNullOrEmpty(settings.ApiKey))
        {
            settings.DeepSeekApiKey = settings.ApiKey;
            settings.DeepSeekChatModel = settings.ChatModel;
            settings.ApiKey = string.Empty;
            settings.ChatModel = string.Empty;
            Save(settings);
            Logger.Info("DEEPSEEK | settings migrated to provider format");
        }

        return settings;
    }

    // В памяти модель остаётся с открытым ключом (VM читает GetActive()); шифруется
    // только копия, уходящая в файл. base.: иначе Save(settings) разрешается в саму
    // себя — бесконечная рекурсия.
    public void Save(DeepSeekSettingsModel settings)
    {
        try
        {
            var snapshot = new DeepSeekSettingsModel
            {
                ActiveProvider = settings.ActiveProvider,
                DeepSeekApiKey = Protect(settings.DeepSeekApiKey),
                DeepSeekChatModel = settings.DeepSeekChatModel,
                OpenRouterApiKey = Protect(settings.OpenRouterApiKey),
                OpenRouterChatModel = settings.OpenRouterChatModel,
                CloudflareApiKey = Protect(settings.CloudflareApiKey),
                CloudflareAccountId = settings.CloudflareAccountId,
                CloudflareChatModel = settings.CloudflareChatModel,
                ApiKey = Protect(settings.ApiKey),
                ChatModel = settings.ChatModel,
            };
            base.Save(snapshot);
        }
        catch (Exception exception)
        {
            // Контракт store: ошибки сохранения не роняют вызывающий код. Открытый
            // текст при сбое шифрования на диск не попадает — файл просто не
            // перезаписывается, ключ остаётся только в памяти.
            Logger.Warn("DEEPSEEK | key encrypt failed | " + System.IO.Path.GetFileName(Path) + " | " + exception.Message);
        }
    }

    public void ClearActive(DeepSeekSettingsModel settings)
    {
        settings.SetActive(string.Empty, string.Empty);
        Save(settings);
        Logger.Info("DEEPSEEK | api key removed | provider=" + settings.ActiveProvider);
    }

    private void DecryptKeys(DeepSeekSettingsModel settings)
    {
        settings.DeepSeekApiKey = Unprotect(settings.DeepSeekApiKey);
        settings.OpenRouterApiKey = Unprotect(settings.OpenRouterApiKey);
        settings.CloudflareApiKey = Unprotect(settings.CloudflareApiKey);
        // Ключ старого формата тоже может прийти шифрованным после миграций.
        settings.ApiKey = Unprotect(settings.ApiKey);
    }

    private string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(encrypted);
    }

    // Сбой расшифровки → пустая строка (fail-closed): повреждённый шифротекст или
    // данные другого пользователя не превращаются в мусорный ключ. Без маркера —
    // значение как есть: settings.json старого формата остаётся читаемым, ключ
    // зашифруется при следующем сохранении.
    private string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var encrypted = Convert.FromBase64String(value[DpapiPrefix.Length..]);
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception exception)
        {
            Logger.Warn("DEEPSEEK | key decrypt failed | " + System.IO.Path.GetFileName(Path) + " | " + exception.Message);
            return string.Empty;
        }
    }
}

// История чатов: список сессий целиком в одном файле (объём мал —
// лимит ограничивает количество сессий и сообщений в них).
public sealed class DeepSeekChatsService : DeepSeekJsonStore
{
    private const int MaxSessions = 100;
    private const int MaxMessagesPerSession = 1000;

    private List<DeepSeekChatSession> _sessions;

    public DeepSeekChatsService(Logger logger, string? filePath = null)
        : base(logger, filePath ?? DeepSeekDataPaths.ChatsFile)
    {
        _sessions = Load<List<DeepSeekChatSession>>([]) ?? [];
    }

    public IReadOnlyList<DeepSeekChatSession> Sessions => _sessions;

    public void SaveSession(DeepSeekChatSession session)
    {
        _sessions.RemoveAll(existing => string.Equals(existing.Id, session.Id, StringComparison.Ordinal));
        // Самые свежие чаты — в начале списка истории.
        _sessions.Insert(0, session);
        Trim(session);
        Save(_sessions);
    }

    public void Remove(string sessionId)
    {
        _sessions.RemoveAll(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
        Save(_sessions);
    }

    private void Trim(DeepSeekChatSession session)
    {
        if (session.Messages.Count > MaxMessagesPerSession)
        {
            session.Messages.RemoveRange(0, session.Messages.Count - MaxMessagesPerSession);
        }

        if (_sessions.Count > MaxSessions)
        {
            _sessions.RemoveRange(MaxSessions, _sessions.Count - MaxSessions);
        }
    }
}
