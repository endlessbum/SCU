using System.Text.Json;
using SCU.AppCore.AI;
using SCU.AppCore.Help;
using SCU.Infrastructure.Logging;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// П. 34C ТЗ: аргументы tools валидируются приложением, а не моделью —
// неизвестная функция, неизвестное значение и неверное направление
// применения отвергаются до того, как что-либо изменится.
public class ScuAiToolValidationTests
{
    private static readonly Logger Logger = Logger.CreateForCurrentRun();

    private static ScuAiContext Context() => new(
        "SCU", "0.0.0", "ru", true, null, string.Empty, null, null,
        "deepseek", "deepseek-chat", true);

    // Тумблер, применяемый только в положение «Выкл.» (как утилиты SCU).
    private static ScuAiCapability ToggleCapability(string utilityId) => new()
    {
        UtilityId = utilityId,
        Title = "Тестовая функция",
        Section = 5,
        Risk = ScuAiRiskLevel.Mutate,
        DesiredState = "Выкл.",
        DesiredStateIsOn = false,
        IsOperation = false,
        ReadState = () => "Вкл.",
        Apply = () => throw new InvalidOperationException("tool не должен выполняться"),
    };

    private static ScuPrepareChangeTool CreateTool(params ScuAiCapability[] capabilities)
    {
        var registry = new ScuAiCapabilityRegistry(Logger);
        foreach (var capability in capabilities)
        {
            registry.InjectForTest(capability);
        }

        return new ScuPrepareChangeTool(new ScuAiToolDeps
        {
            Logger = Logger,
            Help = new StubHelpService(),
            Plans = new ScuAiActionPlanStore(Logger),
            Capabilities = registry,
            Environment = new StubEnvironment(),
            State = null!,
        });
    }

    private static ScuAiExecutionContext ExecutionContext() => new()
    {
        Context = Context(),
        CancellationToken = CancellationToken.None,
    };

    private sealed class StubHelpService : IScuHelpService
    {
        public IReadOnlyList<ScuHelpEntry> All => [];
        public ScuHelpEntry? Get(string id) => null;
        public IReadOnlyList<ScuHelpEntry> Search(string query, int maxResults = 5) => [];
    }

    private sealed class StubEnvironment : IScuAiEnvironment
    {
        public int? CurrentSectionNumber => null;
        public string CurrentSectionTitle => string.Empty;
        public string? CurrentUtilityId => null;
        public string? CurrentUtilityTitle => null;
        public bool IsAdmin => true;
        public IReadOnlyList<int> AvailableSections => [];
        public bool NavigateToSection(int sectionNumber) => false;
        public void HighlightUtility(int sectionNumber, string? utilityTitle) { }
    }

    [Fact]
    public async Task Prepare_UnknownUtilityId_Rejected()
    {
        // П. 34C.11: функции с таким id нет в реестре SCU — план не создаётся,
        // никакого действия не происходит.
        var tool = CreateTool(ToggleCapability("real_feature"));

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"utility_id":"definitely_not_here"}""").RootElement,
            ExecutionContext());

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.CapabilityNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Prepare_InvalidDesiredStateValue_Rejected()
    {
        // П. 34C.13: desired_state — не «on»/«off», а мусорное значение —
        // валидация на стороне приложения.
        var tool = CreateTool(ToggleCapability("animations"));

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"utility_id":"animations","desired_state":"maybe"}""").RootElement,
            ExecutionContext());

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ValidationFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Prepare_OppositeDirection_Rejected()
    {
        // Защита от подмены действия: SCU применяет тумблер только в «Выкл.» —
        // запрос «on» для такой функции невозможен (п. 8D ТЗ).
        var tool = CreateTool(ToggleCapability("animations"));

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"utility_id":"animations","desired_state":"on"}""").RootElement,
            ExecutionContext());

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ValidationFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Prepare_ValidToggle_ReturnsConfirmationWithoutMutation()
    {
        // П. 34D.16/17: валидный prepare создаёт план и отдаёт action_plan_id,
        // сама система не меняется (Apply не вызывается ни разу).
        var capability = ToggleCapability("animations");
        var plans = new ScuAiActionPlanStore(Logger);
        var registry = new ScuAiCapabilityRegistry(Logger);
        registry.InjectForTest(capability);

        var tool = new ScuPrepareChangeTool(new ScuAiToolDeps
        {
            Logger = Logger,
            Help = new StubHelpService(),
            Plans = plans,
            Capabilities = registry,
            Environment = new StubEnvironment(),
            State = null!,
        });

        var executionContext = ExecutionContext();
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"utility_id":"animations","desired_state":"off"}""").RootElement,
            executionContext);

        Assert.True(result.Success);
        Assert.True(result.RequiresConfirmation);
        Assert.False(string.IsNullOrEmpty(result.ActionPlanId));
        Assert.Single(executionContext.Plans);
    }
}
