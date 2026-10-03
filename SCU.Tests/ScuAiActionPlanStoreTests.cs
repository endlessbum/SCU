using System.Text.Json;
using SCU.AppCore.AI;
using SCU.Infrastructure.Logging;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// П. 34D ТЗ: lifecycle ActionPlan — prepare не меняет систему, apply требует
// точного PlanId, подтверждения и совпадения состояния; повторное применение
// и истёкший план невозможны.
public class ScuAiActionPlanStoreTests
{
    private static readonly Logger Logger = Logger.CreateForCurrentRun();

    private static ScuAiActionPlan CreatePlan(ScuAiActionPlanStore store, string state) =>
        store.Create(new ScuAiActionPlanTemplate(
            ToolName: "prepare_scu_change",
            UtilityId: "row_animations",
            UtilityTitle: "Анимации",
            CurrentState: state,
            DesiredState: "Выкл.",
            Risk: ScuAiRiskLevel.Mutate,
            UserVisibleSummary: "Анимации: Вкл. → Выкл.",
            DetailedChanges: "Отключаются анимации окон.",
            RequiresElevation: false,
            RequiresRestart: false));

    [Fact]
    public void Create_Plan_GetsStableId()
    {
        var store = new ScuAiActionPlanStore(Logger);

        var plan = CreatePlan(store, "Вкл.");

        Assert.False(string.IsNullOrEmpty(plan.PlanId));
        Assert.True(plan.ExpiresAt > plan.CreatedAt);
    }

    [Fact]
    public void Apply_WithoutConfirmation_Rejected()
    {
        // П. 34D.22: неподтверждённый план не выполняется — даже если у модели
        // есть PlanId (подтверждение приходит от пользователя, не от модели).
        var store = new ScuAiActionPlanStore(Logger);
        var plan = CreatePlan(store, "Вкл.");

        Assert.Null(store.TryClaimForApply(plan.PlanId, "Вкл."));
    }

    [Fact]
    public void Apply_AfterConfirmation_WithSameState_Claimed()
    {
        var store = new ScuAiActionPlanStore(Logger);
        var plan = CreatePlan(store, "Вкл.");
        store.Confirm(plan.PlanId);

        var claimed = store.TryClaimForApply(plan.PlanId, "Вкл.");

        Assert.NotNull(claimed);
    }

    [Fact]
    public void Apply_StateMismatch_Rejected()
    {
        // П. 34D.21: состояние изменилось после preview — применять вслепую нельзя.
        var store = new ScuAiActionPlanStore(Logger);
        var plan = CreatePlan(store, "Вкл.");
        store.Confirm(plan.PlanId);

        Assert.Null(store.TryClaimForApply(plan.PlanId, "Выкл."));
    }

    [Fact]
    public void Apply_SamePlanTwice_Rejected()
    {
        // П. 34D.19: план одноразовый.
        var store = new ScuAiActionPlanStore(Logger);
        var plan = CreatePlan(store, "Вкл.");
        store.Confirm(plan.PlanId);
        store.TryClaimForApply(plan.PlanId, "Вкл.");
        store.MarkApplied(plan.PlanId);

        Assert.Null(store.TryClaimForApply(plan.PlanId, "Вкл."));
    }

    [Fact]
    public void Apply_UnknownPlan_Rejected()
    {
        var store = new ScuAiActionPlanStore(Logger);

        Assert.Null(store.TryClaimForApply("plan_does_not_exist", "Вкл."));
    }

    [Fact]
    public void Cancel_RemovesPlan()
    {
        // Отмена пользователем: план больше не валиден ни для кого.
        var store = new ScuAiActionPlanStore(Logger);
        var plan = CreatePlan(store, "Вкл.");
        store.Confirm(plan.PlanId);
        store.Cancel(plan.PlanId);

        Assert.False(store.IsConfirmed(plan.PlanId));
        Assert.Null(store.TryClaimForApply(plan.PlanId, "Вкл."));
    }

    // Аудит 3, п. 5: отмена плана в состоянии Applying отклоняется — мутация
    // уже идёт, жизненный цикл завершает executor (MarkApplied), а не Cancel.
    [Fact]
    public void Cancel_WhileApplying_Rejected()
    {
        var store = new ScuAiActionPlanStore(Logger);
        var plan = CreatePlan(store, "Вкл.");
        store.Confirm(plan.PlanId);
        Assert.NotNull(store.TryClaimForApply(plan.PlanId, "Вкл.", out _));

        Assert.False(store.Cancel(plan.PlanId));

        // План жив и подтверждён: executor доводит операцию до конца.
        Assert.NotNull(store.Get(plan.PlanId));
        Assert.True(store.IsConfirmed(plan.PlanId));

        store.MarkApplied(plan.PlanId);
        Assert.Null(store.Get(plan.PlanId));
        Assert.False(store.IsConfirmed(plan.PlanId));
    }

    [Fact]
    public void Confirm_UnknownPlan_Rejected()
    {
        var store = new ScuAiActionPlanStore(Logger);

        Assert.False(store.Confirm("plan_does_not_exist"));
    }

    [Fact]
    public void Apply_ExpiredPlan_Rejected()
    {
        // П. 34D.20: план живёт ограниченное время — даже подтверждённый, но
        // протухший план не выполняется (подтверждение не «вечно»).
        var store = new ScuAiActionPlanStore(Logger);
        var template = new ScuAiActionPlanTemplate(
            "prepare_scu_change", "fake", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false);
        var plan = store.Create(template, DateTime.Now.AddMinutes(-15), DateTime.Now.AddMinutes(-5));
        store.Confirm(plan.PlanId);

        Assert.Null(store.TryClaimForApply(plan.PlanId, "Вкл."));
    }
}

// П. 34C ТЗ: registry tools — allowlist, валидация аргументов, unknown tool.
public class ScuAiToolRegistryTests
{
    private static readonly Logger Logger = Logger.CreateForCurrentRun();

    // Простой test-tool: записывает, что и с какими аргументами выполняли.
    private sealed class TestTool : IScuAiTool
    {
        public string Name => "test_tool";

        public string Description => "Тестовый tool.";

        public ScuAiToolSchema Schema => new(Name, Description,
            """{"type":"object","properties":{"value":{"type":"string"}}}""");

        public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

        public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
        {
            ExecutedArguments = arguments;
            return Task.FromResult(ScuAiToolResult.Ok("{\"ok\":true}"));
        }

        public JsonElement ExecutedArguments { get; private set; }
    }

    // Tool с мутацией: требует подтверждения (п. 7 ТЗ).
    private sealed class TestMutateTool : IScuAiTool
    {
        public string Name => "test_mutate";

        public string Description => "Тестовый tool.";

        public ScuAiToolSchema Schema => new(Name, Description, "{}");

        public ScuAiRiskLevel Risk => ScuAiRiskLevel.Mutate;

        public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context) =>
            throw new NotImplementedException();
    }

    private static ScuAiContext ToolCallingContext() => new(
        "SCU", "1.0.0", "ru", true, 8, "Питание", null, null, "deepseek", "deepseek-chat", true);

    private static ScuAiContext NoToolCallingContext() => new(
        "SCU", "1.0.0", "ru", true, 8, "Питание", null, null, "cloudflare", "model", false);

    [Fact]
    public void Find_UnknownTool_ReturnsNull()
    {
        var registry = new ScuAiToolRegistry(Logger, [new TestTool()]);

        Assert.Null(registry.Find("execute_command"));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_RejectsWithoutExecution()
    {
        // П. 34C.10/38.3: неизвестный tool не выполняется — arbitrary command
        // execution невозможен: такого tool просто нет в allowlist.
        var registry = new ScuAiToolRegistry(Logger, [new TestTool()]);

        var result = await registry.ExecuteAsync(
            "execute_command", "{\"command\":\"format c:\"}", Context(ToolCallingContext()));

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.UnknownTool, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedJson_Rejected()
    {
        // П. 34C.12: мусорный JSON — структурированный отказ, не fake success.
        var registry = new ScuAiToolRegistry(Logger, [new TestTool()]);

        var result = await registry.ExecuteAsync(
            "test_tool", "{ not json", Context(ToolCallingContext()));

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.InvalidArguments, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ValidArguments_ExecutedAndParsed()
    {
        var tool = new TestTool();
        var registry = new ScuAiToolRegistry(Logger, [tool]);

        var result = await registry.ExecuteAsync(
            "test_tool", """{"value":"hello"}""", Context(ToolCallingContext()));

        Assert.True(result.Success);
        Assert.Equal("hello", tool.ExecutedArguments.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_MutatingToolWithoutToolCalling_Rejected()
    {
        // П. 34E.27: провайдер/модель без tool calling не может менять настройки —
        // опасного fallback к «текстовому выполнению» нет.
        var registry = new ScuAiToolRegistry(Logger, [new TestMutateTool()]);

        var result = await registry.ExecuteAsync(
            "test_mutate", "{}", Context(NoToolCallingContext()));

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ProviderUnsupported, result.ErrorCode);
    }

    [Fact]
    public void GetSchemas_WithoutToolCalling_OmitsMutatingTools()
    {
        // П. 26: модели без tool calling отдаются только read-only/navigate схемы.
        var registry = new ScuAiToolRegistry(Logger, [new TestTool(), new TestMutateTool()]);

        var withSupport = registry.GetSchemas(true);
        var withoutSupport = registry.GetSchemas(false);

        Assert.Contains(registry.All, tool => tool.Name == "test_mutate");
        Assert.Equal(2, withSupport.Count);
        Assert.Single(withoutSupport);
        Assert.Equal("test_tool", withoutSupport[0].Name);
    }

    [Fact]
    public async Task ExecuteAsync_ToolThrows_ReturnsStructuredFailure()
    {
        // П. 15/33: внутренняя ошибка tool не превращается в выдуманный успех.
        var registry = new ScuAiToolRegistry(Logger, [new ThrowingTool()]);

        var result = await registry.ExecuteAsync("throwing_tool", "{}", Context(ToolCallingContext()));

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ExecutionFailed, result.ErrorCode);
    }

    private sealed class ThrowingTool : IScuAiTool
    {
        public string Name => "throwing_tool";
        public string Description => "Тестовый tool.";
        public ScuAiToolSchema Schema => new(Name, Description, "{}");
        public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

        public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context) =>
            throw new InvalidOperationException("boom");
    }

    private static ScuAiExecutionContext Context(ScuAiContext context) =>
        new() { Context = context, CancellationToken = CancellationToken.None };
}
