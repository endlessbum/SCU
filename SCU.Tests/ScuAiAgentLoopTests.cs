using System.Text.Json;
using SCU.AppCore.AI;
using SCU.AppCore.Help;
using SCU.Infrastructure.DeepSeek;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// П. 34E/F ТЗ: работа агентного цикла как такового — без HTTP, на заглушке
// IScuAiAgentStepClient. Покрывается:
// - 24: разбор tool_calls из ответа провайдера;
// - 25: результат tool возвращается модели на следующем шаге;
// - 26: лимит итераций останавливает цикл;
// - 28: API-ключ не попадает в системный промпт и в сообщения;
// - 5/6 (повтор): out-of-scope вопрос не уходит на API вообще.
public class ScuAiAgentLoopTests
{
    private static readonly Logger Logger = Logger.CreateForCurrentRun();

    // Контекст, не требующий настоящей инфраструктуры SCU.
    private sealed class StubContextBuilder : IScuAiContextBuilder
    {
        public ScuAiContext Build() => new(
            "SCU", "0.0.0", "ru", false, 0, "Главная",
            null, null, "deepseek", "deepseek-chat", true);
    }

    // Минимальное окружение: разделы есть, навигация никуда не ходит.
    private sealed class StubEnvironment : IScuAiEnvironment
    {
        public int? CurrentSectionNumber => 0;
        public string CurrentSectionTitle => "Главная";
        public string? CurrentUtilityId => null;
        public string? CurrentUtilityTitle => null;
        public bool IsAdmin => false;
        public IReadOnlyList<int> AvailableSections => [0];
        public bool NavigateToSection(int sectionNumber) => false;
        public void HighlightUtility(int sectionNumber, string? utilityTitle) { }
    }

    // Read-only tool-заглушка: не касается системы, возвращает известный ответ.
    private sealed class ProbeTool : IScuAiTool
    {
        public const string Answer = "probe-answer-42";

        public int Calls { get; private set; }

        public string Name => "probe_read";
        public string Description => "probe";
        public ScuAiToolSchema Schema => new(Name, Description, "{}");
        public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

        public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
        {
            Calls++;
            return Task.FromResult(ScuAiToolResult.Ok($"{{\"answer\":\"{Answer}\"}}"));
        }
    }

    // Заглушка шага API: ответы по порядку; запоминает, что уходило на шаг.
    private sealed class FakeAgentClient : IScuAiAgentStepClient
    {
        private readonly Queue<DeepSeekClient.AssistantCompletion> _responses = new();

        // Все шаги, полученные от ScuAiAssistant (системный промпт, история, schemas).
        public List<(string SystemPrompt, string MessagesJson, int ToolsCount)> Steps { get; } = [];

        public FakeAgentClient(params DeepSeekClient.AssistantCompletion[] responses)
        {
            foreach (var response in responses)
            {
                _responses.Enqueue(response);
            }
        }

        public Task<DeepSeekClient.AssistantCompletion> SendAgentStepAsync(
            string apiKey, string model, string systemPrompt,
            IReadOnlyList<ScuAiAgentMessage> messages,
            IReadOnlyList<ScuAiToolSchema> tools,
            DeepSeekProviderConfig provider, bool supportsToolCalling,
            CancellationToken cancellationToken = default)
        {
            Steps.Add((systemPrompt, JsonSerializer.Serialize(messages.Select(message => new
            {
                message.Role,
                message.Content,
                tool_calls = message.ToolCalls?.Count,
                message.ToolCallId,
            })), tools.Count));

            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : DeepSeekClient.AssistantCompletion.Fail("fake: unexpected step"));
        }
    }

    private static ScuAiToolCall ToolCall(string id) => new(id, "probe_read", "{}");

    // Tool, прикрепляющий к контексту UI-событие (как prepare_scu_change
    // прикрепляет карточку плана) и возвращающий успешный результат.
    private sealed class CardTool : IScuAiTool
    {
        public const string EventMessage = "card-event-from-tool";

        public string Name => "card_probe";
        public string Description => "probe";
        public ScuAiToolSchema Schema => new(Name, Description, "{}");
        public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

        public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
        {
            context.AttachFailure(Name, EventMessage);
            return Task.FromResult(ScuAiToolResult.Ok("{}"));
        }
    }

    // Собирает помощника с заглушкой API; отдаёт и сам фейковый клиент, и пробу.
    private static (ScuAiAssistant assistant, FakeAgentClient client, ProbeTool probe) CreateAssistant(
        DeepSeekClient.AssistantCompletion[] responses, params IScuAiTool[] extraTools)
    {
        var probe = new ProbeTool();
        var client = new FakeAgentClient(responses);
        var registry = new ScuAiToolRegistry(Logger, [probe, .. extraTools]);
        var assistant = new ScuAiAssistant(
            Logger,
            client,
            registry,
            new StubContextBuilder(),
            new ScuAiPlanExecutor(new ScuAiToolDeps
            {
                Logger = Logger,
                Help = new StubHelpService(),
                Plans = new ScuAiActionPlanStore(Logger),
                Capabilities = new ScuAiCapabilityRegistry(Logger),
                Environment = new StubEnvironment(),
                State = null!,
            }),
            new StubEnvironment(),
            new StubHelpService());

        return (assistant, client, probe);
    }

    private sealed class StubHelpService : IScuHelpService
    {
        public IReadOnlyList<ScuHelpEntry> All => [];
        public ScuHelpEntry? Get(string id) => null;
        public IReadOnlyList<ScuHelpEntry> Search(string query, int maxResults = 5) => [];
    }

    [Fact]
    public async Task AgentLoop_FeedBack_ContainsToolResultOnNextStep()
    {
        // П. 34E.24/25: первый шаг — tool_call, выполненный приложением tool
        // возвращает результат; второй шаг получает его как role=tool.
        var (assistant, client, probe) = CreateAssistant(
        [
            new(true, string.Empty, "tool_calls", [ToolCall("call_1")], 0, string.Empty, [], "deepseek-chat"),
            new(true, "Готово: probe-answer-42", "stop", [], 0, string.Empty, [], "deepseek-chat"),
        ]);

        var turn = await assistant.AskAsync(
            "sk-test-key-1234567890abcdef", "deepseek-chat", DeepSeekProviderConfig.DeepSeek,
            "Что скажешь?", [], CancellationToken.None);

        Assert.Equal(1, probe.Calls);
        Assert.Equal("Готово: probe-answer-42", turn.Text);

        // Второй шаг получил историю: assistant с tool_calls + tool-сообщение
        // с результатом для call_1 (п. 34E.25).
        Assert.Equal(2, client.Steps.Count);
        var secondStep = client.Steps[1];
        Assert.Contains("\"Role\":\"tool\"", secondStep.MessagesJson);
        Assert.Contains("call_1", secondStep.MessagesJson);
        Assert.Contains("probe-answer-42", secondStep.MessagesJson);
        Assert.Contains("\"Role\":\"assistant\"", secondStep.MessagesJson);
        Assert.Contains("\"tool_calls\":1", secondStep.MessagesJson);
    }

    [Fact]
    public async Task AgentLoop_IterationLimit_StopsTheCycle()
    {
        // П. 34E.26: модель бесконечно зовёт tool — цикл останавливается по
        // лимиту ScuAiPolicy.MaxAgentIterations, ход заканчивается ошибкой.
        var responses = Enumerable.Repeat(
            new DeepSeekClient.AssistantCompletion(
                true, string.Empty, "tool_calls", [ToolCall("call_loop")], 0, string.Empty, [], "deepseek-chat"),
            ScuAiPolicy.MaxAgentIterations + 5).ToArray();

        var (assistant, client, probe) = CreateAssistant(responses);

        var turn = await assistant.AskAsync(
            "sk-test-key", "deepseek-chat", DeepSeekProviderConfig.DeepSeek,
            "Зациклись", [], CancellationToken.None);

        Assert.True(turn.IsError);
        Assert.Equal(ScuAiPolicy.MaxAgentIterations, client.Steps.Count);
        Assert.Equal(ScuAiPolicy.MaxAgentIterations, probe.Calls);
    }

    [Fact]
    public async Task AgentLoop_ApiKey_NeverEntersPromptOrMessages()
    {
        // П. 34F.28: ключ живёт только в заголовке авторизации — ни системный
        // промпт, ни сообщения его не содержат (проверка на стороне приложения,
        // а не на словах провайдера).
        const string key = "sk-supersecretkey0001234567890abcdef";
        var (assistant, client, _) = CreateAssistant(
            [new(true, "Ответ", "stop", [], 0, string.Empty, [], "deepseek-chat")]);
        await assistant.AskAsync(
            key, "deepseek-chat", DeepSeekProviderConfig.DeepSeek,
            "Расскажи про анимации", [], CancellationToken.None);

        var step = Assert.Single(client.Steps);
        Assert.DoesNotContain(key, step.SystemPrompt);
        Assert.DoesNotContain(key, step.MessagesJson);
        Assert.DoesNotContain("sk-", step.SystemPrompt);
        Assert.DoesNotContain("sk-", step.MessagesJson);
    }

    [Fact]
    public async Task AgentLoop_UiEventsFromEarlierIteration_ReachTheTurnResult()
    {
        // Карточки, собранные tools на ранней итерации (план от prepare_scu_change,
        // справка от search_scu_help), доезжают до UI вместе с финальным текстом,
        // а не теряются между шагами цикла.
        var (assistant, _, _) = CreateAssistant(
            [
                new(true, string.Empty, "tool_calls", [new ScuAiToolCall("call_1", "card_probe", "{}")], 0, string.Empty, [], "deepseek-chat"),
                new(true, "Готово", "stop", [], 0, string.Empty, [], "deepseek-chat"),
            ],
            new CardTool());

        var turn = await assistant.AskAsync(
            "sk-test-key", "deepseek-chat", DeepSeekProviderConfig.DeepSeek,
            "Подготовь изменение", [], CancellationToken.None);

        Assert.False(turn.IsError);
        Assert.Equal("Готово", turn.Text);
        var failure = Assert.Single(turn.Failures);
        Assert.Equal(CardTool.EventMessage, failure.Message);
    }

    [Fact]
    public async Task AgentLoop_WithOpenRouterConfig_RunsFullModeWithToolSchemas()
    {
        // OpenRouter поддерживает function calling (в отличие от Cloudflare):
        // помощник работает в полном режиме — схемы tools уходят в API.
        var (assistant, client, _) = CreateAssistant(
            [new(true, "Ответ", "stop", [], 0, string.Empty, [], "openrouter/model")]);

        // Форматно-валидный ключ собирается в рантайме: литерал вида
        // sk-or-v1-<64 hex> в исходнике блокируется GitHub Push Protection.
        await assistant.AskAsync(
            "sk-or-v1-" + new string('0', 64),
            "deepseek/deepseek-chat", DeepSeekProviderConfig.OpenRouter,
            "Что скажешь?", [], CancellationToken.None);

        var step = Assert.Single(client.Steps);
        Assert.True(step.ToolsCount > 0);
    }

    [Fact]
    public async Task Ask_OutOfScopeQuestion_NeverCallsApi()
    {
        // П. 15/34B: погода — вне scope SCU: canned-ответ отдаётся локально,
        // HTTP-запрос к провайдеру не уходит (ни одного шага в заглушке).
        var (assistant, client, _) = CreateAssistant(
            [new(true, "не должно быть вызвано", "stop", [], 0, string.Empty, [], "deepseek-chat")]);

        var turn = await assistant.AskAsync(
            "sk-test-key", "deepseek-chat", DeepSeekProviderConfig.DeepSeek,
            "Какая погода в Москве?", [], CancellationToken.None);

        Assert.Empty(client.Steps);
        Assert.False(string.IsNullOrEmpty(turn.Text));
        Assert.DoesNotContain("погода", turn.Text, StringComparison.OrdinalIgnoreCase);
    }
}
