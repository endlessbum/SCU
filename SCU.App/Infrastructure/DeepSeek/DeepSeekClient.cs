using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SCU.Models.AI;
using SCU.Models.DeepSeek;

namespace SCU.Infrastructure.DeepSeek;

// Провайдер OpenAI-совместимого API с моделями DeepSeek.
// DeepSeek — официальный API (ключ проверяется авторизованным запросом,
// список моделей требует ключ). OpenRouter — агрегатор с бесплатными
// вариантами моделей («:free»): список моделей публичный, поэтому ключ
// проверяется отдельным запросом GET /auth/key. Cloudflare Workers AI —
// постоянный бесплатный tier (Neurons): токен проверяется через
// /user/tokens/verify, base URL включает Account ID аккаунта.
public sealed record DeepSeekProviderConfig(
    string Id, string BaseUrl, string AuthCheckUrl, bool ModelsRequireAuth)
{
    public const string CloudflareId = "cloudflare";

    public static readonly DeepSeekProviderConfig DeepSeek =
        new("deepseek", "https://api.deepseek.com", "https://api.deepseek.com/models", true);

    public static readonly DeepSeekProviderConfig OpenRouter =
        new("openrouter", "https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/auth/key", false);

    // Готовый конфиг Cloudflare под конкретный аккаунт.
    public static DeepSeekProviderConfig Cloudflare(string accountId) =>
        new(CloudflareId,
            $"https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/v1",
            "https://api.cloudflare.com/client/v4/user/tokens/verify",
            false);

    public static DeepSeekProviderConfig FromId(string? id, string? accountId = null) => id switch
    {
        "openrouter" => OpenRouter,
        CloudflareId => Cloudflare(accountId ?? string.Empty),
        _ => DeepSeek,
    };
}

// Один шаг агентного цикла как контракт: ScuAiAssistant зависит от интерфейса,
// а не от конкретного клиента — в тестах подменяется заглушкой, чтобы
// проверять лимит итераций, возврат tool-результатов и непопадание секретов
// в системный промпт (п. 34E/F). Реализация — DeepSeekClient.SendAgentStepAsync.
public interface IScuAiAgentStepClient
{
    Task<DeepSeekClient.AssistantCompletion> SendAgentStepAsync(
        string apiKey,
        string model,
        string systemPrompt,
        IReadOnlyList<ScuAiAgentMessage> messages,
        IReadOnlyList<ScuAiToolSchema> tools,
        DeepSeekProviderConfig provider,
        bool supportsToolCalling,
        CancellationToken cancellationToken = default);
}

// Клиент API DeepSeek/OpenRouter. Ошибки не выбрасываются наружу: они
// логируются с тегом DEEPSEEK и возвращаются в Result — вызывающий код
// показывает статус, приложение живёт.
public sealed partial class DeepSeekClient : IScuAiAgentStepClient
{
    // Приоритет моделей чата по провайдерам: у DeepSeek — актуальная flash-модель
    // (канонический ID после переименования V4.1 Flash) → предыдущие; у OpenRouter —
    // бесплатные варианты deepseek («:free»). Legacy-идентификаторы
    // deepseek-chat/deepseek-reasoner/deepseek-v4.1-flash не включены: DeepSeek
    // отключил их в API (после 24.07.2026 запросы возвращают ошибку; v4.1-flash
    // переименован в deepseek-flash).
    private static readonly string[] DeepSeekPreferredModels =
    [
        "deepseek-flash",
        "deepseek-v4-flash",
    ];

    private static readonly string[] OpenRouterPreferredModels =
    [
        // Бесплатные варианты («:free») появляются у OpenRouter периодически —
        // пробуем сначала их, затем стабильные платные (дешёвая flash-модель первой).
        "deepseek/deepseek-v4-flash:free",
        "deepseek/deepseek-v4.1-flash:free",
        "deepseek/deepseek-chat-v3.1:free",
        "deepseek/deepseek-v4-flash",
        "deepseek/deepseek-v4.1-flash",
        "deepseek/deepseek-v3.2",
    ];

    // Cloudflare Workers AI: эндпоинт /models у compat-API не документирован —
    // статический список DeepSeek-моделей из каталога Workers AI. Порядок —
    // от доступных на Workers Free плане: v4-flash/v4-pro Free-план
    // разворачивает (403 «not available on the Workers Free plan»), поэтому
    // первым идёт r1-дистиллят; при 403 клиент откатывается на следующую.
    public static readonly string[] CloudflareModels =
    [
        "@cf/deepseek-ai/deepseek-r1-distill-qwen-32b",
        "@cf/deepseek-ai/deepseek-v4-flash-0731",
        "@cf/deepseek-ai/deepseek-v4-pro-0813",
    ];

    // ===================== Совместимость и capabilities =====================

    // Миграция legacy-моделей (аудит совместимости): DeepSeek отключил
    // deepseek-chat/deepseek-reasoner после 24.07.2026 — запросы к ним
    // возвращают ошибку; канонический ID V4.1 Flash после переименования —
    // deepseek-flash. Сохранённые у пользователей модели переезжают на
    // актуальные идентификаторы (нечего мигрировать → возвращается как есть).
    // Обратная совместимость: deepseek-v4-flash принимается API как есть.
    public static readonly Dictionary<string, string> DeprecatedModelRemap = new(StringComparer.Ordinal)
    {
        ["deepseek-chat"] = "deepseek-v4-flash",
        ["deepseek-reasoner"] = "deepseek-v4-pro",
        ["deepseek-v4.1-flash"] = "deepseek-flash",
    };

    public static string MigrateDeprecatedModel(string? model) =>
        model is not null && DeprecatedModelRemap.TryGetValue(model, out var replacement)
            ? replacement
            : model ?? string.Empty;

    // Модели Cloudflare с заявленной поддержкой function calling: V4 Flash/Pro —
    // да; r-дистилляты — нет (игнорируют схему / отвечают 400). У DeepSeek и
    // OpenRouter function calling есть у всех chat/reasoning-моделей.
    private static readonly HashSet<string> CloudflareToolCallingModels = new(StringComparer.Ordinal)
    {
        "@cf/deepseek-ai/deepseek-v4-flash-0731",
        "@cf/deepseek-ai/deepseek-v4-pro-0813",
    };

    // Capability — по паре провайдер+модель (аудит AI): определение только по
    // провайдеру навсегда отключало agent tools у Cloudflare, хотя его V4-модели
    // tool calling поддерживают.
    public static bool SupportsToolCalling(DeepSeekProviderConfig provider, string? model)
    {
        if (provider.Id is "deepseek" or "openrouter")
        {
            return true;
        }

        return provider.Id == DeepSeekProviderConfig.CloudflareId
               && model is not null
               && CloudflareToolCallingModels.Contains(model);
    }

    // Формат ключей: DeepSeek — sk- + 32 hex; OpenRouter — sk-or-v1- + 64 hex;
    // Cloudflare — API-токен: нового формата с префиксом cfut_ либо 40 символов
    // [A-Za-z0-9_-] без префикса.
    private static readonly Regex DeepSeekKeyFormat = new(@"^sk-[0-9a-fA-F]{32}$", RegexOptions.Compiled);
    private static readonly Regex OpenRouterKeyFormat = new(@"^sk-or-v1-[0-9a-fA-F]{64}$", RegexOptions.Compiled);
    private static readonly Regex CloudflareKeyFormat = new(@"^(cfut_[A-Za-z0-9_-]{40,}|[A-Za-z0-9_-]{40})$", RegexOptions.Compiled);

    // Account ID Cloudflare — 32 hex-символа.
    private static readonly Regex CloudflareAccountIdFormat = new(@"^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

    // Общий клиент без таймаута: лимит ставится на каждый запрос через CTS —
    // проверка ключа короткая, а генерация ответа может идти больше минуты.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly TimeSpan KeyCheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromMinutes(3);

    private readonly Logger _logger;

    public DeepSeekClient(Logger logger)
    {
        _logger = logger;
    }

    public static bool IsKeyFormatValid(string key, DeepSeekProviderConfig provider) =>
        (provider.Id switch
        {
            DeepSeekProviderConfig.CloudflareId => CloudflareKeyFormat,
            "openrouter" => OpenRouterKeyFormat,
            _ => DeepSeekKeyFormat,
        }).IsMatch(key);

    public static bool IsCloudflareAccountIdValid(string accountId) =>
        CloudflareAccountIdFormat.IsMatch(accountId);

    // Результат проверки ключа: Models — доступные ключу id моделей (для выбора
    // модели чата). Error — русский шаблон (возможно, с {0}); аргументы в ErrorArgs,
    // чтобы вызывающий код локализовал его через L.T уже с подстановкой.
    public sealed record KeyCheck(
        bool IsValid, string Error, object?[] ErrorArgs, IReadOnlyList<string> Models)
    {
        public static readonly KeyCheck NetworkError =
            new(false, "Нет соединения с провайдером — проверьте интернет.", [], []);
    }

    public async Task<KeyCheck> ValidateKeyAsync(
        string apiKey, DeepSeekProviderConfig provider, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(KeyCheckTimeout);

            // Шаг 1: проверка ключа. У OpenRouter /models публичный — проверяем
            // по /auth/key; у DeepSeek проверка и список моделей совмещены;
            // у Cloudflare — /user/tokens/verify.
            _logger.Info($"DEEPSEEK | validating key | provider={provider.Id}");
            using var authResponse = await SendAsync(provider.AuthCheckUrl, apiKey, provider, timeout.Token)
                .ConfigureAwait(false);
            var authBody = await authResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!authResponse.IsSuccessStatusCode)
            {
                _logger.Warn($"DEEPSEEK | key rejected | http {(int)authResponse.StatusCode} | {Trim(authBody)}");
                return KeyFailure((int)authResponse.StatusCode, provider);
            }

            IReadOnlyList<string> models;
            if (provider.Id == DeepSeekProviderConfig.CloudflareId)
            {
                // {"success":true,"result":{"status":"active",...}} — статус обязателен.
                if (!IsTokenVerifyOk(authBody))
                {
                    _logger.Warn("DEEPSEEK | cloudflare token rejected | " + Trim(authBody));
                    return new KeyCheck(
                        false, "Токен отклонён Cloudflare — проверьте токен и разрешения Workers AI.", [], []);
                }

                models = CloudflareModels;
            }
            else if (provider.ModelsRequireAuth)
            {
                models = ParseModelIds(authBody, model => true);
            }
            else
            {
                // Шаг 2 (OpenRouter): публичный список моделей — фильтруем deepseek.
                using var modelsResponse = await SendAsync(provider.BaseUrl + "/models", apiKey, provider, timeout.Token)
                    .ConfigureAwait(false);
                if (modelsResponse.IsSuccessStatusCode)
                {
                    var body = await modelsResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    models = ParseModelIds(body, IsOpenRouterDeepSeekModel);
                }
                else
                {
                    _logger.Warn("DEEPSEEK | model list unavailable | http " + (int)modelsResponse.StatusCode);
                    models = [];
                }
            }

            _logger.Info($"DEEPSEEK | key accepted | provider={provider.Id} | models={models.Count}");
            return new KeyCheck(true, string.Empty, [], models);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or OperationCanceledException or IOException)
        {
            // TaskCanceledException при внешней отмене не маскируем сетевой ошибкой.
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.Error("DEEPSEEK | key validation failed | " + exception.Message);
            return KeyCheck.NetworkError;
        }
    }

    private static bool IsTokenVerifyOk(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            // Fail-closed: поле status обязательно и обязано равняться «active».
            // Ответ без status (или с нестроковым) раньше трактовался как
            // «токен активен» — проверка противоречила собственному комментарию.
            return document.RootElement.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True
                && document.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "active", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ===================== Типобезопасные читатели JSON =====================

    // Читатели полей без сюрпризов (аудит AI): GetString()/GetInt64()/
    // EnumerateArray() на поле неожиданного типа бросают InvalidOperationException/
    // FormatException, которых нет в catch-списках — malformed 200-ответ
    // превращался в необработанное исключение вместо Fail. Любое несовпадение
    // схемы трактуется как отсутствие значения.
    private static string SafeGetString(in JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static long SafeGetInt64(in JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
    }

    // Ответ чата: Content — текст ассистента, Error — русский шаблон с ErrorArgs.
    // Model — модель, чей ответ вернулся: при автоматическом откате (Cloudflare
    // 403 «not available on plan») может отличаться от запрошенной.
    // ModelUnavailable — модель закрыта планом аккаунта: обёртка откатывается
    // на следующую модель списка CloudflareModels.
    public sealed record ChatReply(
        bool IsSuccess, string Content, string Error, object?[] ErrorArgs,
        string Model = "", bool ModelUnavailable = false)
    {
        public static ChatReply Fail(string error, params object?[] args) => new(false, string.Empty, error, args);
    }

    // ===================== Agent / tool calling =====================

    // Capability layer (п. 26 ТЗ): провайдер ≠ модель ≠ capability.
    // Поддержка определяется по паре провайдер+модель — см. SupportsToolCalling.

    // Ответ одного шага агентного цикла (п. 12 ТЗ): либо текст, либо tool_calls,
    // либо то и другое. Дальше решает вызывающий код (ScuAiAssistant): валидация
    // и выполнение tools — на стороне приложения, а не модели.
    public sealed record AssistantCompletion(
        bool IsSuccess,
        string Content,
        string FinishReason,
        IReadOnlyList<ScuAiToolCall> ToolCalls,
        long UsageTokens,
        string Error,
        object?[] ErrorArgs,
        string Model = "")
    {
        public bool HasToolCalls => ToolCalls.Count > 0;

        public static AssistantCompletion Fail(string error, params object?[] args) =>
            new(false, string.Empty, string.Empty, [], 0, error, args);
    }

    // Один шаг agent-цикла: messages + system + tools → ответ модели. Сами tools
    // не выполняются — ScuAiAssistant валидирует их и исполнит, а сюда вернётся
    // уже с результатами на следующей итерации (п. 12 ТЗ).
    public async Task<AssistantCompletion> SendAgentStepAsync(
        string apiKey,
        string model,
        string systemPrompt,
        IReadOnlyList<ScuAiAgentMessage> messages,
        IReadOnlyList<ScuAiToolSchema> tools,
        DeepSeekProviderConfig provider,
        bool supportsToolCalling,
        CancellationToken cancellationToken = default)
    {
        try
        {
            const int MaxOutputTokens = 4096;

            var payload = BuildAgentPayload(
                model, MaxOutputTokens, systemPrompt, messages,
                supportsToolCalling && tools.Count > 0 ? tools : null);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, provider.BaseUrl + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, AgentJsonOptions), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            ApplyProviderHeaders(request, provider);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ChatTimeout);

            _logger.Info($"SCU_AI | agent step | model={model} | messages={messages.Count} | tools={payload.Tools?.Length ?? 0}");
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"SCU_AI | agent step failed | http {(int)response.StatusCode} | {Trim(body)}");
                var chatFailure = ChatFailure((int)response.StatusCode, body, provider);
                return AssistantCompletion.Fail(chatFailure.Error, chatFailure.ErrorArgs);
            }

            var (completion, usageTokens) = ParseAgentCompletion(body, model);

            if (completion.IsSuccess)
            {
                _logger.Info($"SCU_AI | agent step ok | finish={completion.FinishReason} | tool_calls={completion.ToolCalls.Count} | tokens={usageTokens}");
            }

            return completion;
        }
        // InvalidOperationException/FormatException — страховка на случай
        // непокрытой хелперами схемы (аудит AI): сбойный 200-ответ → Fail,
        // а не необработанное исключение.
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or OperationCanceledException or IOException or JsonException
                                              or InvalidOperationException or FormatException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.Error("SCU_AI | agent step failed | " + exception.Message);
            return AssistantCompletion.Fail(exception is TaskCanceledException or OperationCanceledException
                ? "Время ожидания ответа истекло."
                : exception is HttpRequestException or IOException
                    ? "Нет соединения с провайдером — проверьте интернет."
                    : "Некорректный ответ провайдера — повторите запрос.");
        }
    }

    // Разбор ответа одного шага (OpenAI-compatible): content, finish_reason,
    // tool_calls, usage. Выделен отдельно, чтобы тесты проверяли разбор
    // tool_calls на фиксированных телах ответов без HTTP (п. 34E.24).
    internal static (AssistantCompletion Completion, long UsageTokens) ParseAgentCompletion(
        string body, string model)
    {
        using var document = JsonDocument.Parse(body);
        return ParseAgentCompletion(document.RootElement, model);
    }

    private static (AssistantCompletion Completion, long UsageTokens) ParseAgentCompletion(
        JsonElement root, string model)
    {
        // Robustness (аудит AI): сбойный 200-ответ (шлюз, обёртка, пустой choices)
        // возвращает Fail, а не KeyNotFoundException поверх catch-списка — тот же
        // класс, что исправлен в ParseModelIds.
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return (AssistantCompletion.Fail("Некорректный ответ провайдера: нет массива choices."), 0);
        }

        var choice = choices.EnumerateArray().FirstOrDefault();
        if (choice.ValueKind != JsonValueKind.Object)
        {
            return (AssistantCompletion.Fail("Некорректный ответ провайдера: пустой choices."), 0);
        }

        var message = choice.TryGetProperty("message", out var messageElement) ? messageElement : default;
        if (message.ValueKind != JsonValueKind.Object)
        {
            return (AssistantCompletion.Fail("Некорректный ответ провайдера: нет message в choices."), 0);
        }

        // Схема нарушена явно (content/tool_calls не того типа) → Fail, а не
        // «успех с пустым значением»: пустой финальный ответ в цикле считался
        // бы ошибкой, а потерянные tool_calls привели бы к невалидной истории.
        string content;
        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            return (AssistantCompletion.Fail("Некорректный ответ провайдера: поле content неожиданного типа."), 0);
        }

        content = SafeGetString(message, "content");

        // Reasoning-модели: служебный блок размышлений не идёт ни в чат, ни в
        // историю agent-цикла (иначе следующий шаг получил бы свой же мусор).
        content = StripThinking(content);
        content = string.IsNullOrWhiteSpace(content) ? string.Empty : StripMarkup(content);

        var finishReason = SafeGetString(choice, "finish_reason");

        IReadOnlyList<ScuAiToolCall> toolCalls = [];
        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("tool_calls", out var callsElement)
            && callsElement.ValueKind != JsonValueKind.Undefined)
        {
            if (callsElement.ValueKind != JsonValueKind.Array)
            {
                return (AssistantCompletion.Fail("Некорректный ответ провайдера: поле tool_calls неожиданного типа."), 0);
            }

            toolCalls = callsElement.EnumerateArray().Select(ParseToolCall).ToArray();
        }

        var usageTokens = SafeGetInt64(
            root.TryGetProperty("usage", out var usageElement) ? usageElement : default, "total_tokens");

        return (new AssistantCompletion(true, content, finishReason, toolCalls, usageTokens, string.Empty, [], model),
            usageTokens);
    }

    // Разбор tool_call из ответа: id нужен, чтобы вернуть результат именно этому
    // вызову; аргументы остаются сырым JSON — валидируются приложением.
    private static ScuAiToolCall ParseToolCall(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new ScuAiToolCall(string.Empty, string.Empty, "{}");
        }

        var id = SafeGetString(element, "id");
        var name = string.Empty;
        var arguments = "{}";
        if (element.TryGetProperty("function", out var functionElement)
            && functionElement.ValueKind == JsonValueKind.Object)
        {
            name = SafeGetString(functionElement, "name");
            arguments = functionElement.TryGetProperty("arguments", out var argsElement)
                ? argsElement.ValueKind == JsonValueKind.String
                    ? argsElement.GetString() ?? "{}"
                    : argsElement.GetRawText()
                : "{}";
        }

        return new ScuAiToolCall(id, name, arguments);
    }

    // Сборка payload агентного запроса — вынесена для регрессионного теста:
    // формат тела запроса проверяется на фиксированных сообщениях без HTTP.
    internal static AgentPayload BuildAgentPayload(
        string model,
        int maxTokens,
        string systemPrompt,
        IReadOnlyList<ScuAiAgentMessage> messages,
        IReadOnlyList<ScuAiToolSchema>? tools) =>
        new()
        {
            Model = model,
            MaxTokens = maxTokens,
            Messages = [new AgentMessage("system", systemPrompt), .. messages.Select(ToAgentMessage)],
            Tools = tools is { Count: > 0 } ? [.. tools.Select(tool => new AgentTool(tool))] : null,
        };

    // Перевод сообщения агентного цикла в формат API: tool-сообщения несут
    // tool_call_id и name, assistant с вызовами — список tool_calls. Пустой
    // content у assistant-сообщения с tool_calls не отправляется (лишает
    // провайдера повода ответить 400, п. 12 аудита).
    internal static AgentMessage ToAgentMessage(ScuAiAgentMessage message)
    {
        var agentMessage = new AgentMessage(message.Role, string.IsNullOrEmpty(message.Content) ? null : message.Content)
        {
            ToolCallId = message.ToolCallId,
            Name = message.Name,
            ToolCalls = message.ToolCalls is { Count: > 0 } calls
                ? calls
                    .Select(call => new AgentToolCall
                    {
                        Id = call.Id,
                        Function = new AgentFunction { Name = call.Name, Arguments = call.ArgumentsJson },
                    })
                    .ToList()
                : null,
        };

        return agentMessage;
    }

    // Payload agent-запроса: системный промпт + история + схемы tools.
    // Опции сериализации: null-поля опускаются — провайдер отвечает 400 на
    // пустой content вместе с tool_calls (п. 12 аудита). Имена полей заданы
    // явными атрибутами: дефолтная сериализация ушла бы в API PascalCase-ключами
    // («Model», «Messages», «ToolCallId»), что Cloudflare отвергает 400-й
    // («Cloudflare не принял запрос»), а tool-поля требуют snake_case OpenAI
    // (tool_call_id, tool_calls), не camelCase.
    internal static readonly JsonSerializerOptions AgentJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal sealed class AgentPayload
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream => false;

        [JsonPropertyName("messages")]
        public required AgentMessage[] Messages { get; init; }

        [JsonPropertyName("tools")]
        public AgentTool[]? Tools { get; init; }
    }

    internal sealed class AgentMessage
    {
        public AgentMessage(string role, string? content)
        {
            Role = role;
            Content = content;
        }

        [JsonPropertyName("role")]
        public string Role { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("tool_calls")]
        public List<AgentToolCall>? ToolCalls { get; init; }
    }

    internal sealed class AgentToolCall
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("function")]
        public AgentFunction Function { get; init; } = new();
    }

    internal sealed class AgentFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; init; } = "{}";
    }

    // Описание tool для API: схема отдаётся как готовый JSON (ScuAiSchemaBuilder).
    internal sealed class AgentTool
    {
        public AgentTool(ScuAiToolSchema schema)
        {
            Function = new AgentToolFunction
            {
                Name = schema.Name,
                Description = schema.Description,
                Parameters = JsonNode.Parse(schema.ParametersJson),
            };
        }

        [JsonPropertyName("type")]
        public string Type => "function";

        [JsonPropertyName("function")]
        public AgentToolFunction Function { get; }
    }

    internal sealed class AgentToolFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("parameters")]
        public JsonNode? Parameters { get; init; }
    }

    // Отправка переписки (не-стриминговый режим: ждём весь ответ целиком).
    public async Task<ChatReply> SendChatAsync(        string apiKey,
        string model,
        IReadOnlyList<DeepSeekChatMessage> history,
        DeepSeekProviderConfig provider,
        CancellationToken cancellationToken = default)
    {
        var reply = await SendChatCoreAsync(apiKey, model, history, provider, cancellationToken)
            .ConfigureAwait(false);

        // Cloudflare: модель закрыта планом — та же переписка уходит следующей
        // модели списка (состав Free-плана меняется, порядок остаётся актуален).
        if (reply.ModelUnavailable && provider.Id == DeepSeekProviderConfig.CloudflareId)
        {
            var candidates = CloudflareModels
                .SkipWhile(candidate => !string.Equals(candidate, model, StringComparison.Ordinal))
                .Skip(1);
            foreach (var candidate in candidates)
            {
                _logger.Warn($"DEEPSEEK | model unavailable, falling back | {model} -> {candidate}");
                reply = await SendChatCoreAsync(apiKey, candidate, history, provider, cancellationToken)
                    .ConfigureAwait(false);
                if (!reply.ModelUnavailable)
                {
                    return reply;
                }
            }
        }

        return reply;
    }

    private async Task<ChatReply> SendChatCoreAsync(
        string apiKey,
        string model,
        IReadOnlyList<DeepSeekChatMessage> history,
        DeepSeekProviderConfig provider,
        CancellationToken cancellationToken)
    {
        try
        {
            // Явный лимит вывода: без него провайдеры режут ответ небольшим
            // дефолтом — у reasoning-моделей лимит уходит в <think>, и ответ
            // обрывается на полуслове (или «пустой ответ»).
            const int MaxOutputTokens = 4096;

            // Системная подсказка против «мусора» в чате: без рендеринга
            // LaTeX/Markdown модельный ответ выглядит как \times и **Шаг 1**.
            const string SystemPrompt =
                "Ты — полезный ассистент в чате приложения SCU. " +
                "Отвечай на языке вопроса пользователя. " +
                "Пиши простым текстом без разметки: не используй LaTeX (\\(, \\), \\[, \\], $), " +
                "жирный шрифт (**), заголовки (#) и списки с дефисами. " +
                "Формулы записывай обычными символами, например: 6 × 4 = 24.";

            var payload = new
            {
                model,
                max_tokens = MaxOutputTokens,
                messages = new[] { new { role = "system", content = SystemPrompt } }
                    .Concat(history
                        .Where(message => !message.IsError)
                        .Select(message => new { role = message.Role, content = message.Content }))
                    .ToArray(),
                stream = false,
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post, provider.BaseUrl + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            ApplyProviderHeaders(request, provider);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ChatTimeout);

            _logger.Info($"DEEPSEEK | chat request | provider={provider.Id} | model={model} | messages={payload.messages.Length}");
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"DEEPSEEK | chat failed | http {(int)response.StatusCode} | {Trim(body)}");
                return ChatFailure((int)response.StatusCode, body, provider);
            }

            using var document = JsonDocument.Parse(body);
            // Robustness (аудит AI): сбойный 200-ответ без choices (ошибка-в-теле,
            // пустой JSON, обёртка шлюза) — честный Fail, а не KeyNotFoundException
            // поверх catch-списка и молча пропавший ход пользователя.
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array)
            {
                _logger.Warn("DEEPSEEK | chat failed | no choices in 200 response | " + Trim(body));
                return ChatReply.Fail("Некорректный ответ провайдера: нет блока choices.");
            }

            var choice = choices.EnumerateArray().FirstOrDefault();
            if (choice.ValueKind != JsonValueKind.Object)
            {
                _logger.Warn("DEEPSEEK | chat failed | empty choices in 200 response | " + Trim(body));
                return ChatReply.Fail("Некорректный ответ провайдера: пустой блок choices.");
            }

            // M-08: типы вложенных полей проверяются явно — GetString()/GetInt64()
            // на значении не того типа бросают InvalidOperationException, который
            // раньше не ловился и ронял ход вместо честного ChatReply.Fail.
            var content = choice.TryGetProperty("message", out var message)
                          && message.ValueKind == JsonValueKind.Object
                          && message.TryGetProperty("content", out var text)
                          && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? string.Empty
                : string.Empty;

            // Reasoning-модели (deepseek-r1) предваряют ответ служебным
            // блоком размышлений — в чате остаётся только сам ответ.
            content = StripThinking(content);

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.Warn("DEEPSEEK | chat failed | empty completion | " + Trim(body));
                return ChatReply.Fail(
                    "Модель ушла в размышления и не успела дать ответ — попробуйте повторить вопрос.");
            }

            // Страховка от остатков разметки, если модель проигнорировала
            // системную подсказку: LaTeX-скобки, жирный шрифт, заголовки, $$.
            content = StripMarkup(content);

            // L-01: ответ мог состоять только из разметки — после очистки он
            // пуст, и возвращать «успех» с пустым текстом нельзя.
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.Warn("DEEPSEEK | chat failed | empty completion after markup strip | " + Trim(body));
                return ChatReply.Fail(
                    "Ответ модели состоял только из разметки — попробуйте повторить вопрос.");
            }

            long usage = 0;
            if (document.RootElement.TryGetProperty("usage", out var usageElement)
                && usageElement.ValueKind == JsonValueKind.Object
                && usageElement.TryGetProperty("total_tokens", out var tokens)
                && tokens.ValueKind == JsonValueKind.Number
                && tokens.TryGetInt64(out var parsedTokens))
            {
                usage = parsedTokens;
            }
            _logger.Info($"DEEPSEEK | chat ok | tokens={usage}");
            return new ChatReply(true, content, string.Empty, [], model);
        }
        catch (InvalidOperationException exception)
        {
            // M-08: структурно некорректный (но валидный JSON) ответ провайдера —
            // честный Fail, а не необработанное исключение поверх catch-списка.
            _logger.Error("DEEPSEEK | chat parse failed | " + exception.Message);
            return ChatReply.Fail("Некорректный ответ провайдера: неожиданная структура JSON.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or OperationCanceledException or IOException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.Error("DEEPSEEK | chat request failed | " + exception.Message);
            return ChatReply.Fail(exception is TaskCanceledException or OperationCanceledException
                ? "Время ожидания ответа истекло."
                : "Нет соединения с провайдером — проверьте интернет.");
        }
    }

    // Выбор модели чата из списка, доступного ключу: приоритет — список
    // предпочтительных, иначе первая модель из ответа API.
    public static string PickChatModel(IReadOnlyList<string> availableModels, DeepSeekProviderConfig provider)
    {
        var preferred = provider.Id switch
        {
            DeepSeekProviderConfig.CloudflareId => CloudflareModels,
            "openrouter" => OpenRouterPreferredModels,
            _ => DeepSeekPreferredModels,
        };
        foreach (var candidate in preferred)
        {
            if (availableModels.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return availableModels.Count > 0 ? availableModels[0] : preferred[0];
    }

    // ===================== Внутреннее =====================

    private static async Task<HttpResponseMessage> SendAsync(
        string url, string apiKey, DeepSeekProviderConfig provider, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        ApplyProviderHeaders(request, provider);
        return await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // Рекомендованные OpenRouter заголовки атрибуции (не влияют на DeepSeek).
    private static void ApplyProviderHeaders(HttpRequestMessage request, DeepSeekProviderConfig provider)
    {
        if (provider.Id == DeepSeekProviderConfig.OpenRouter.Id)
        {
            request.Headers.Add("HTTP-Referer", "https://github.com/endlessbum/SCU");
            request.Headers.TryAddWithoutValidation("X-Title", "SCU");
        }
    }

    // DeepSeek-модели OpenRouter: пропускаем batch-варианты и алиасы «~…» —
    // бесплатные («:free») попадают вместе с платными, выбор делает PickChatModel.
    private static bool IsOpenRouterDeepSeekModel(string id) =>
        id.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
        && !id.EndsWith(":batch", StringComparison.OrdinalIgnoreCase)
        && !id.StartsWith("~", StringComparison.Ordinal);

    // Устойчивость к нестандартному ответу (аудит robustness): отсутствие «data»,
    // не-массив и не-объекты не бросают исключений — GetProperty бросает
    // KeyNotFoundException (не JsonException), EnumerateArray на не-массиве —
    // InvalidOperationException, оба раньше улетали мимо catch.
    internal static IReadOnlyList<string> ParseModelIds(string json, Func<string, bool> filter)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null)
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()
                .Where(filter)
                .ToList();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
    }

    private static KeyCheck KeyFailure(int status, DeepSeekProviderConfig provider)
    {
        if (provider.Id == DeepSeekProviderConfig.CloudflareId)
        {
            return status switch
            {
                401 or 403 => new KeyCheck(
                    false, "Токен отклонён Cloudflare — проверьте токен и разрешения Workers AI.", [], []),
                429 => new KeyCheck(
                    false, "Бесплатный лимит Neurons на сегодня исчерпан — попробуйте завтра.", [], []),
                400 => new KeyCheck(
                    false, "Cloudflare не принял запрос — проверьте Account ID аккаунта.", [], []),
                _ => new KeyCheck(false, "Провайдер вернул ошибку (HTTP {0}).", [status], []),
            };
        }

        if (provider.Id == "openrouter")
        {
            return status switch
            {
                401 or 403 => new KeyCheck(
                    false, "Ключ отклонён сервером — проверьте, что скопирован верный API-ключ.", [], []),
                429 => new KeyCheck(
                    false, "Лимит бесплатных запросов исчерпан — попробуйте завтра или пополните баланс OpenRouter.", [], []),
                _ => new KeyCheck(false, "Провайдер вернул ошибку (HTTP {0}).", [status], []),
            };
        }

        return status switch
        {
            401 or 403 => new KeyCheck(
                false, "Ключ отклонён сервером — проверьте, что скопирован верный API-ключ.", [], []),
            429 => new KeyCheck(false, "Слишком много запросов к API — попробуйте позже.", [], []),
            _ => new KeyCheck(false, "Провайдер вернул ошибку (HTTP {0}).", [status], []),
        };
    }

    private static ChatReply ChatFailure(int status, string body, DeepSeekProviderConfig provider)
    {
        if (provider.Id == DeepSeekProviderConfig.CloudflareId)
        {
            // Модель закрыта планом аккаунта (Free): маркер «not available on … plan».
            if (status == 403 && body.Contains("not available on the", StringComparison.Ordinal))
            {
                return new ChatReply(
                    false,
                    string.Empty,
                    "Модель недоступна на текущем плане Cloudflare.",
                    [],
                    ModelUnavailable: true);
            }

            return status switch
            {
                401 or 403 => ChatReply.Fail("Токен отклонён Cloudflare — проверьте токен и разрешения Workers AI."),
                429 or 402 => ChatReply.Fail("Бесплатный лимит Neurons на сегодня исчерпан — попробуйте завтра."),
                400 => ChatReply.Fail("Cloudflare не принял запрос — проверьте Account ID аккаунта."),
                _ => ChatReply.Fail("Провайдер вернул ошибку (HTTP {0}).", status),
            };
        }

        if (provider.Id == "openrouter" && status == 429)
        {
            return ChatReply.Fail(
                "Лимит бесплатных запросов исчерпан — попробуйте завтра или пополните баланс OpenRouter.");
        }

        return status switch
        {
            401 or 403 => ChatReply.Fail("Ключ больше не действителен — переподключите API-ключ в разделе AI."),
            402 => ChatReply.Fail("Недостаточно средств на балансе аккаунта провайдера."),
            429 => ChatReply.Fail("Слишком много запросов к API — попробуйте позже."),
            _ => ChatReply.Fail("Провайдер вернул ошибку (HTTP {0}).", status),
        };
    }

    // Служебные размышления reasoning-моделей: <think>…</think> в начале ответа.
    private static string StripThinking(string content)
    {
        if (!content.Contains("<think>", StringComparison.Ordinal))
        {
            return content;
        }

        var start = content.IndexOf("<think>", StringComparison.Ordinal);
        var end = content.IndexOf("</think>", StringComparison.Ordinal);
        if (end < 0)
        {
            // Незакрытый блок (обрыв ответа) — убираем от <think> до конца.
            return content[..start].TrimStart();
        }

        end += "</think>".Length;
        return (content[..start] + content[end..]).TrimStart();
    }

    // Чистка разметки в ответе: рендеринга в чате нет, поэтому LaTeX и
    // Markdown приводятся к обычному тексту. Дистилляты r1 продолжают
    // писать формулы LaTeX даже по системной подсказке «без разметки» —
    // конвертируем частые конструкции (\frac, \sqrt, \times и т.д.),
    // остальное имена команд просто убираем.
    // Лёгкая чистка разметки в ответе: LaTeX и Markdown → обычный текст.
    // Любая ошибка здесь не должна ронять чат: возвращаем ответ как есть.
    private static string StripMarkup(string content)
    {
        try
        {
            return StripMarkupCore(content);
        }
        catch (Exception exception)
        {
            return "Исходный ответ модели:\n\n" + content + "\n\n(не удалось очистить разметку: "
                + exception.Message + ")";
        }
    }

    private static string StripMarkupCore(string content)
    {
        // \frac{a}{b} → (a)/(b) — цикл для вложенных дробей.
        var frac = new Regex(@"\\frac\{([^{}]*)\}\{([^{}]*)\}");
        while (frac.IsMatch(content))
        {
            content = frac.Replace(content, "($1)/($2)");
        }

        content = new Regex(@"\\sqrt\{([^{}]*)\}").Replace(content, "√($1)");
        content = new Regex(@"\\text\{([^{}]*)\}").Replace(content, "$1");

        // Имена операторов: литеральный бэкслеш + имя + граница слова
        // (@"\\" даёт в regex '\\': именно «обратный слэш», иначе \t читался
        // бы как табуляция, а \c и \i бросали «Unrecognized escape sequence»).
        (string Command, string Symbol)[] symbols =
        [
            ("times", "×"), ("div", "÷"), ("cdot", "·"), ("ne", "≠"),
            ("le", "≤"), ("ge", "≥"), ("approx", "≈"), ("pm", "±"),
            ("infty", "∞"), ("pi", "π"), ("alpha", "α"), ("beta", "β"),
        ];
        foreach (var (command, symbol) in symbols)
        {
            content = Regex.Replace(content, @"\\" + command + @"\b", symbol);
        }

        // Math-ограничители: \( \) \[ \] $$.
        content = Regex.Replace(content, @"\\[()\[\]]", string.Empty);
        content = content.Replace("$$", string.Empty);
        content = content.Replace("**", string.Empty).Replace("__", string.Empty);
        content = Regex.Replace(content, @"(?m)^[ \t]{0,3}#{1,6}[ \t]+", string.Empty);

        // Остатки LaTeX-окружений и неизвестных команд (\begin{…}, \left…).
        content = Regex.Replace(content, @"\\(begin|end)\{[^{}]*\}", string.Empty);
        content = Regex.Replace(content, @"\\[a-zA-Z]+", string.Empty);
        return content.Trim();
    }

    private static string Trim(string body)
    {
        var singleLine = body.ReplaceLineEndings(" ");
        return singleLine.Length <= 300 ? singleLine : singleLine[..300] + "…";
    }
}
