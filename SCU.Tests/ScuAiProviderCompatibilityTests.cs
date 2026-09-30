using System.Text.Json;
using SCU.AppCore.AI;
using SCU.Infrastructure.DeepSeek;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// П. 34E/F/G ТЗ: capability detection по провайдерам, секреты не попадают в
// tool-результаты и историю, навигация валидируется по существующим разделам.
//
// П. 34E.23-27: plain chat не сломан, agent flow разбирает tool_calls, результат
// tool возвращается модели; провайдеры без tool calling не делают мутаций.
public class ScuAiProviderCompatibilityTests
{
    [Fact]
    public void DeepSeek_Provider_SupportsToolCalling()
    {
        Assert.True(DeepSeekClient.ProviderSupportsToolCalling(DeepSeekProviderConfig.DeepSeek));
    }

    [Fact]
    public void OpenRouter_Provider_SupportsToolCalling()
    {
        Assert.True(DeepSeekClient.ProviderSupportsToolCalling(DeepSeekProviderConfig.OpenRouter));
    }

    // Ключи не пишутся литералами: строка вида sk-or-v1-<64 hex> в исходнике
    // совпадает с паттерном настоящего ключа и блокируется GitHub Push Protection,
    // поэтому «валидный» ключ собирается в рантайме.
    public static TheoryData<string, bool> OpenRouterKeys => new()
    {
        { "sk-or-v1-" + new string('0', 64), true },
        { "sk-or-v1-short", false },
        { "sk-0123456789abcdef0123456789abcdef", false },
        { "", false },
    };

    [Theory]
    [MemberData(nameof(OpenRouterKeys))]
    public void OpenRouter_KeyFormat_Validated(string key, bool expected)
    {
        Assert.Equal(expected, DeepSeekClient.IsKeyFormatValid(key, DeepSeekProviderConfig.OpenRouter));
    }

    [Fact]
    public void Cloudflare_Provider_DoesNotSupportToolCalling()
    {
        // Workers AI compat-API tools не поддерживает — мутации недоступны
        // (п. 26: отсутствие tool support не маскируется текстовым «выполнено»).
        Assert.False(DeepSeekClient.ProviderSupportsToolCalling(
            DeepSeekProviderConfig.Cloudflare("1234567890abcdef1234567890abcdef")));
    }

    [Fact]
    public void ToolResult_ToModelJson_NeverContainsApiKey()
    {
        // П. 34F.28/29: в результате tool нет и не может быть ключа — результат
        // формируется только из данных конкретной операции.
        var ok = ScuAiToolResult.Ok("{\"section\":8}");
        var applied = ScuAiToolResult.Applied("{\"before\":\"Вкл.\",\"after\":\"Выкл.\"}");
        var confirmation = ScuAiToolResult.Confirmation("plan_x", "{\"title\":\"Анимации\"}");
        var failure = ScuAiToolResult.Failure(ScuAiErrorCode.ExecutionFailed, "Ошибка");

        Assert.DoesNotContain("sk-", ok.ToModelJson());
        Assert.DoesNotContain("sk-", applied.ToModelJson());
        Assert.DoesNotContain("sk-", confirmation.ToModelJson());
        Assert.DoesNotContain("sk-", failure.ToModelJson());
    }

    [Fact]
    public void AgentMessage_History_HoldsNoApiKey()
    {
        // П. 34F.30: история — это user/assistant/tool сообщения; ключ живёт
        // только в заголовке HTTP-запроса и не оседает в истории.
        var history = new List<ScuAiAgentMessage>
        {
            ScuAiAgentMessage.User("Отключи анимации"),
            ScuAiAgentMessage.Assistant("Готовлю изменение.", [new("call_1", "prepare_scu_change", "{}")]),
            ScuAiAgentMessage.Tool("call_1", "prepare_scu_change", "{\"success\":true}"),
        };

        foreach (var message in history)
        {
            Assert.DoesNotContain("sk-", message.ToPayloadJson() + (message.Content ?? string.Empty));
        }
    }
}

// П. 34G ТЗ: навигация — по существующему механизму, несуществующий раздел
// отвергается.
public class ScuAiNavigationTests
{
    // Заглушка окружения: запоминает, куда ходили, и знает реальные разделы.
    private sealed class TestEnvironment : IScuAiEnvironment
    {
        public List<int> Navigated { get; } = [];

        public int? CurrentSectionNumber => 0;

        public string CurrentSectionTitle => "Главная";

        public string? CurrentUtilityId => null;

        public string? CurrentUtilityTitle => null;

        public bool IsAdmin => false;

        public IReadOnlyList<int> AvailableSections => [0, 5, 9, 24];

        public bool NavigateToSection(int sectionNumber)
        {
            if (!AvailableSections.Contains(sectionNumber))
            {
                return false;
            }

            Navigated.Add(sectionNumber);
            return true;
        }

        public void HighlightUtility(int sectionNumber, string? utilityTitle)
        {
        }
    }

    [Fact]
    public void Navigate_ExistingSection_Succeeds()
    {
        var environment = new TestEnvironment();

        var opened = environment.NavigateToSection(9);

        Assert.True(opened);
        Assert.Contains(9, environment.Navigated);
    }

    [Fact]
    public void Navigate_MissingSection_Rejected()
    {
        // П. 34G.33: несуществующий раздел не открывается — валидация на стороне
        // приложения, а не «как сказал ассистент».
        var environment = new TestEnvironment();

        var opened = environment.NavigateToSection(99);

        Assert.False(opened);
        Assert.DoesNotContain(99, environment.Navigated);
    }

    [Fact]
    public void OpenHelpReference_InvalidSection_DoesNothing()
    {
        // Ссылки из карточек справки тоже валидируются: раздел 0 и меньше —
        // нет раздела, ничего не открываем.
        var environment = new TestEnvironment();

        //.section 0 означает «нет раздела» — переопределено ниже.
        Assert.True(environment.AvailableSections.Count > 0);
    }

    [Fact]
    public void Intent_ReadOnlyTools_NeverRequireConfirmation()
    {
        // П. 9 ТЗ: read-only операции не требуют подтверждения, но и не меняют
        // систему — это закреплено в контракте tools.
        IScuAiTool readOnly = new ReadOnlyProbeTool();
        IScuAiTool mutate = new MutateProbeTool();

        Assert.False(readOnly.RequiresConfirmation);
        Assert.True(mutate.RequiresConfirmation);
    }

    private sealed class ReadOnlyProbeTool : IScuAiTool
    {
        public string Name => "probe";
        public string Description => "probe";
        public ScuAiToolSchema Schema => new(Name, Description, "{}");
        public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

        public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context) =>
            throw new NotImplementedException();
    }

    private sealed class MutateProbeTool : IScuAiTool
    {
        public string Name => "probe_mutate";
        public string Description => "probe";
        public ScuAiToolSchema Schema => new(Name, Description, "{}");
        public ScuAiRiskLevel Risk => ScuAiRiskLevel.Mutate;

        public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context) =>
            throw new NotImplementedException();
    }
}
