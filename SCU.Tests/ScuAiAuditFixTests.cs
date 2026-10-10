using System.Text.Json;
using SCU.AppCore.AI;
using SCU.AppCore.Help;
using SCU.Infrastructure.DeepSeek;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// Аудит AI: регрессии на найденные проблемы.
// - гонка общего history: все мутации (вопрос пользователя, apply-сообщения,
//   тримминг) должны происходить внутри _turnLock, а не до его захвата;
// - пустой финальный completion — ошибка хода, а не «успех» с пустым текстом;
// - malformed 200-ответ провайдера — Fail с понятной ошибкой, а не необработанное
//   InvalidOperationException/FormatException из GetString()/GetInt64().
public class ScuAiAuditFixTests
{
    private static readonly Logger Logger = Logger.CreateForCurrentRun();

    private sealed class StubContextBuilder : IScuAiContextBuilder
    {
        public ScuAiContext Build() => new(
            "SCU", "0.0.0", "ru", false, 0, "Главная",
            null, null, "deepseek", "deepseek-flash", true);
    }

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

    private sealed class StubHelpService : IScuHelpService
    {
        public IReadOnlyList<ScuHelpEntry> All => [];
        public ScuHelpEntry? Get(string id) => null;
        public IReadOnlyList<ScuHelpEntry> Search(string query, int maxResults = 5) => [];
    }

    // Заглушка шага API: ответ определяется зарегистрированным резолвером,
    // который может ждать (для детерминированного перехлёста двух ходов).
    private sealed class GatedAgentClient : IScuAiAgentStepClient
    {
        public Func<IReadOnlyList<ScuAiAgentMessage>, Task<DeepSeekClient.AssistantCompletion>> Resolver { get; set; } =
            _ => Task.FromResult(DeepSeekClient.AssistantCompletion.Fail("fake: no resolver"));

        public Task<DeepSeekClient.AssistantCompletion> SendAgentStepAsync(
            string apiKey, string model, string systemPrompt,
            IReadOnlyList<ScuAiAgentMessage> messages,
            IReadOnlyList<ScuAiToolSchema> tools,
            DeepSeekProviderConfig provider, bool supportsToolCalling,
            CancellationToken cancellationToken = default)
        {
            return Resolver(messages);
        }
    }

    private static ScuAiAssistant CreateAssistant(GatedAgentClient client)
    {
        return new ScuAiAssistant(
            Logger,
            client,
            new ScuAiToolRegistry(Logger, []),
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
    }

    private static DeepSeekClient.AssistantCompletion Text(string content) =>
        new(true, content, "stop", [], 0, string.Empty, [], "deepseek-flash");

    [Fact]
    public async Task ParallelTurns_SecondTurnMutatesHistoryOnlyAfterFirstCompletes()
    {
        // Аудит AI: history — общая структура. Пока первый ход крутится под
        // _turnLock (шаг API ещё в полёте), второй ход не имеет права добавлять
        // в неё свой вопрос — иначе история первого хода окажется испорченной.
        var client = new GatedAgentClient();
        var assistant = CreateAssistant(client);

        var firstStepEntered = new TaskCompletionSource();
        var releaseFirstStep = new TaskCompletionSource();
        var history = new List<ScuAiAgentMessage>();

        client.Resolver = messages =>
        {
            if (!firstStepEntered.Task.IsCompleted)
            {
                firstStepEntered.SetResult();
                return releaseFirstStep.Task.ContinueWith(
                    _ => Text("Ответ первого хода"), TaskScheduler.Default);
            }

            return Task.FromResult(Text("Ответ второго хода"));
        };

        var first = assistant.AskAsync(
            "sk-test-key", "deepseek-flash", DeepSeekProviderConfig.DeepSeek,
            "Вопрос первого хода", history, CancellationToken.None);

        await firstStepEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = assistant.AskAsync(
            "sk-test-key", "deepseek-flash", DeepSeekProviderConfig.DeepSeek,
            "Вопрос второго хода", history, CancellationToken.None);

        // Даём второму ходу дойти до попытки мутации history.
        await Task.Delay(100);
        Assert.Single(history);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("Вопрос первого хода", history[0].Content);

        releaseFirstStep.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        // Оба хода в истории, без перемешивания: user/assistant парами по порядку.
        Assert.Equal(4, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("Вопрос первого хода", history[0].Content);
        Assert.Equal("assistant", history[1].Role);
        Assert.Equal("Ответ первого хода", history[1].Content);
        Assert.Equal("user", history[2].Role);
        Assert.Equal("Вопрос второго хода", history[2].Content);
        Assert.Equal("assistant", history[3].Role);
        Assert.Equal("Ответ второго хода", history[3].Content);
    }

    [Fact]
    public async Task EmptyFinalCompletion_TurnFails()
    {
        // Аудит AI: пустой финальный completion — не «успешный ход с пустым
        // текстом», а ошибка, как в обычном chat-пути. Пустое assistant-сообщение
        // в историю не попадает.
        var client = new GatedAgentClient { Resolver = _ => Task.FromResult(Text("   ")) };
        var assistant = CreateAssistant(client);
        var history = new List<ScuAiAgentMessage>();

        var turn = await assistant.AskAsync(
            "sk-test-key", "deepseek-flash", DeepSeekProviderConfig.DeepSeek,
            "Молчи", history, CancellationToken.None);

        Assert.True(turn.IsError);
        Assert.False(string.IsNullOrWhiteSpace(turn.Text));
        Assert.Single(history);
        Assert.Equal("user", history[0].Role);
    }

    // ===================== Malformed 200-ответы =====================

    private static void AssertParseFails(string body)
    {
        var (completion, usage) = DeepSeekClient.ParseAgentCompletion(body, "deepseek-flash");

        Assert.False(completion.IsSuccess, $"ожидали Fail для {body}, получили успех");
        Assert.False(string.IsNullOrWhiteSpace(completion.Error));
        Assert.Equal(0, usage);
    }

    [Fact]
    public void ParseAgentCompletion_ContentNotString_Fails()
    {
        AssertParseFails("""{"choices":[{"message":{"content":42},"finish_reason":"stop"}]}""");
    }

    [Fact]
    public void ParseAgentCompletion_ContentNull_Tolerated()
    {
        // content: null — легитимное значение провайдеров при tool_calls.
        var (completion, _) = DeepSeekClient.ParseAgentCompletion(
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"c1","function":{"name":"probe_read","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""",
            "deepseek-flash");

        Assert.True(completion.IsSuccess);
        Assert.Empty(completion.Content);
        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal("probe_read", call.Name);
    }

    [Fact]
    public void ParseAgentCompletion_FinishReasonNotString_Tolerated()
    {
        // finish_reason — семантически не критичен: неверный тип → пустая строка,
        // а не исключение.
        var (completion, _) = DeepSeekClient.ParseAgentCompletion(
            """{"choices":[{"message":{"content":"ok"},"finish_reason":{"code":"stop"}}]}""",
            "deepseek-flash");

        Assert.True(completion.IsSuccess);
        Assert.Equal("ok", completion.Content);
        Assert.Empty(completion.FinishReason);
    }

    [Fact]
    public void ParseAgentCompletion_ToolCallsNotArray_Fails()
    {
        AssertParseFails("""{"choices":[{"message":{"content":null,"tool_calls":{"id":"c1"}},"finish_reason":"stop"}]}""");
    }

    [Fact]
    public void ParseAgentCompletion_ToolCallIdNotString_NoException()
    {
        var (completion, _) = DeepSeekClient.ParseAgentCompletion(
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":7,"function":{"name":"probe_read","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""",
            "deepseek-flash");

        Assert.True(completion.IsSuccess);
        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal(string.Empty, call.Id);
        Assert.Equal("probe_read", call.Name);
    }

    [Fact]
    public void ParseAgentCompletion_FunctionNameNotString_NoException()
    {
        var (completion, _) = DeepSeekClient.ParseAgentCompletion(
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"c1","function":{"name":99,"arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""",
            "deepseek-flash");

        Assert.True(completion.IsSuccess);
        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal(string.Empty, call.Name);
    }

    [Fact]
    public void ParseAgentCompletion_UsageTokensAsString_Tolerated()
    {
        var (completion, usage) = DeepSeekClient.ParseAgentCompletion(
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"total_tokens":"12"}}""",
            "deepseek-flash");

        Assert.True(completion.IsSuccess);
        Assert.Equal(0, usage);
    }

    [Fact]
    public void ParseAgentCompletion_MessageMissing_Fails()
    {
        // choice без message: пустой content → Fail по схеме «нет массива choices»
        // не сработает, поэтому проверяем явный fail без исключения.
        AssertParseFails("""{"choices":[{"finish_reason":"stop"}]}""");
    }

    [Fact]
    public void ParseAgentCompletion_ChoicesEmpty_Fails()
    {
        AssertParseFails("""{"choices":[]}""");
    }

    [Fact]
    public void ParseAgentCompletion_ChoicesNotArray_Fails()
    {
        AssertParseFails("""{"choices":{"message":{}}}""");
    }

    [Fact]
    public void ParseAgentCompletion_ValidBody_ParsesUsage()
    {
        var (completion, usage) = DeepSeekClient.ParseAgentCompletion(
            """{"choices":[{"message":{"content":"Готово"},"finish_reason":"stop"}],"usage":{"total_tokens":128}}""",
            "deepseek-flash");

        Assert.True(completion.IsSuccess);
        Assert.Equal("Готово", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(128, usage);
    }
}
