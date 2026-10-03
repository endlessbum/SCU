using SCU.AppCore.AI;
using SCU.AppCore.Help;
using SCU.Infrastructure.Logging;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// П. 34D/34H ТЗ: end-to-end путь prepare → подтверждение → apply через общий
// исполнитель. Мутации идут только по PlanId, повторно и с устаревшим
// состоянием — никогда.
public class ScuAiPlanExecutorTests
{
    private static readonly Logger Logger = Logger.CreateForCurrentRun();

    // Capability-заглушка: помнит, сколько раз звали Apply.
    private sealed class FakeCapabilityHolder
    {
        public int ApplyCalls { get; private set; }

        public ScuAiCapability Create(string state, ScuAiRiskLevel risk = ScuAiRiskLevel.Mutate) => new()
        {
            UtilityId = "fake",
            Title = "Тест",
            Risk = risk,
            ReadState = () => state,
            Apply = () =>
            {
                ApplyCalls++;
                return Task.FromResult<(bool, string)>((true, "Применено"));
            },
        };
    }

    private static ScuAiCapability CreateFailing() => new()
    {
        UtilityId = "failing",
        Title = "Тест",
        Risk = ScuAiRiskLevel.Mutate,
        ReadState = () => "Вкл.",
        Apply = () => Task.FromResult<(bool, string)>((false, "Отказ раздела")),
    };

    private static (ScuAiActionPlanStore plans, ScuAiPlanExecutor executor) CreateWithCapability(
        ScuAiCapability capability)
    {
        var plans = new ScuAiActionPlanStore(Logger);
        var capabilities = new ScuAiCapabilityRegistry(Logger);
        capabilities.InjectForTest(capability);

        var deps = new ScuAiToolDeps
        {
            Logger = Logger,
            Help = new StubHelpService(),
            Plans = plans,
            Capabilities = capabilities,
            Environment = new StubEnvironment(),
            State = null!, // этим тестам не нужен
        };

        return (plans, new ScuAiPlanExecutor(deps));
    }

    // Минимальные заглушкиdeps: executor не зовёт их, но контракт требует.
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
        public bool IsAdmin => false;
        public IReadOnlyList<int> AvailableSections => [];
        public bool NavigateToSection(int sectionNumber) => false;
        public void HighlightUtility(int sectionNumber, string? utilityTitle) { }
    }

    [Fact]
    public async Task Prepare_DoesNotMutate_System()
    {
        // П. 34D.16: prepare только готовит preview — применения нет.
        var holder = new FakeCapabilityHolder();
        var capability = holder.Create("Вкл.");
        var (plans, _) = CreateWithCapability(capability);

        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", capability.UtilityId, capability.Title,
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));

        await Task.Yield();

        Assert.Equal(0, holder.ApplyCalls);
        Assert.Equal("Вкл.", capability.ReadState());
        Assert.False(string.IsNullOrEmpty(plan.PlanId));
    }

    [Fact]
    public async Task Apply_WithoutConfirmation_DoesNotExecute()
    {
        var holder = new FakeCapabilityHolder();
        var (plans, executor) = CreateWithCapability(holder.Create("Вкл."));
        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "fake", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));

        var outcome = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(0, holder.ApplyCalls);
    }

    [Fact]
    public async Task Apply_WithConfirmation_ExecutesOnceAndReturnsBeforeAfter()
    {
        // Полный путь: план валиден, подтверждён, состояние то же → выполняется
        // ровно один раз, результат несёт было/стало (п. 34D.17/18).
        var holder = new FakeCapabilityHolder();
        var (plans, executor) = CreateWithCapability(holder.Create("Вкл."));
        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "fake", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        var outcome = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Result);
        Assert.Equal(1, holder.ApplyCalls);
        Assert.Equal("Вкл.", outcome.Result!.BeforeState);
        Assert.Equal("Применено", outcome.Result.Status);
    }

    [Fact]
    public async Task Apply_Failure_NotMaskedAsSuccess()
    {
        // П. 15 ТЗ: ошибка tool не превращается в выдуманный успех — план можно
        // повторить, кнопка остаётся.
        var (plans, executor) = CreateWithCapability(CreateFailing());

        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "failing", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        var outcome = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(ScuAiErrorCode.ExecutionFailed, outcome.ErrorCode);
        Assert.Contains("Отказ раздела", outcome.Message);
    }

    [Fact]
    public async Task Apply_StateMismatch_Rejected()
    {
        // План готовился под «Вкл.», а сейчас «Выкл.» — применять вслепую
        // нельзя (п. 34D.21).
        var capability = CreateFailing();
        capability.ReadState = () => "Выкл.";
        var (plans, executor) = CreateWithCapability(capability);

        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "failing", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        var outcome = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(ScuAiErrorCode.StateMismatch, outcome.ErrorCode);
    }

    // ===================== Атомарный claim (аудит 2, п. 18) =====================

    [Fact]
    public async Task Apply_ParallelSamePlan_OnlyOneExecutes()
    {
        // Два ApplyAsync одного PlanId: первый забирает claim (Confirmed →
        // Applying), второй обязан отклониться, пока apply не завершён.
        var holder = new FakeCapabilityHolder();
        var capability = holder.Create("Вкл.");
        var (plans, executor) = CreateWithCapability(capability);
        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "fake", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        // Атомарный claim прямо на store: второй claim того же плана до
        // MarkApplied обязан отклониться с AlreadyApplying.
        var claimed = plans.TryClaimForApply(plan.PlanId, "Вкл.", out var firstReason);
        Assert.NotNull(claimed);

        var outcome = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal(ScuAiErrorCode.StateMismatch, outcome.ErrorCode);
        Assert.Equal(0, holder.ApplyCalls);

        // После сбоя первого apply claim освобождён — план можно применить снова.
        plans.MarkApplyFailed(plan.PlanId);
        var retried = plans.TryClaimForApply(plan.PlanId, "Вкл.", out var retryReason);
        Assert.NotNull(retried);
        Assert.Equal(ScuAiActionPlanStore.ClaimRejectReason.None, retryReason);
    }

    [Fact]
    public async Task Apply_AfterFailure_CanBeRetried()
    {
        // Сбой apply возвращает план в Confirmed (не в Applied): повтор возможен.
        var (plans, executor) = CreateWithCapability(CreateFailing());
        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "failing", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        var first = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);
        Assert.False(first.Success);

        // Повторный apply проходит claim заново (отказ — от capability, не от store).
        var second = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);
        Assert.False(second.Success);
        Assert.Equal(ScuAiErrorCode.ExecutionFailed, second.ErrorCode);
    }

    private static ScuAiCapability CreateThrowing() => new()
    {
        UtilityId = "throwing",
        Title = "Тест",
        Risk = ScuAiRiskLevel.Mutate,
        ReadState = () => "Вкл.",
        Apply = () => throw new InvalidOperationException("отказ операции"),
    };

    // ===================== Жизненный цикл после claim (аудит 3, п. 3/4/6/7) =====================

    [Fact]
    public async Task Apply_Throws_RestoresConfirmedPlan()
    {
        // Исключение capability не оставляет план в Applying навечно: возврат
        // в Confirmed, повторный apply возможен.
        var (plans, executor) = CreateWithCapability(CreateThrowing());
        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "throwing", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ApplyAsync(plan.PlanId, CancellationToken.None));

        Assert.True(plans.IsConfirmed(plan.PlanId));
        Assert.NotNull(plans.TryClaimForApply(plan.PlanId, "Вкл.", out var reason));
        Assert.Equal(ScuAiActionPlanStore.ClaimRejectReason.None, reason);
    }

    [Fact]
    public async Task Apply_CancelledAfterClaim_RestoresConfirmedPlan()
    {
        // Отмена после claim не блокирует план в Applying (аудит 3, п. 3).
        var holder = new FakeCapabilityHolder();
        var (plans, executor) = CreateWithCapability(holder.Create("Вкл."));
        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "fake", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ApplyAsync(plan.PlanId, cts.Token));

        Assert.Equal(0, holder.ApplyCalls);
        Assert.True(plans.IsConfirmed(plan.PlanId));
        Assert.NotNull(plans.TryClaimForApply(plan.PlanId, "Вкл.", out var reason));
        Assert.Equal(ScuAiActionPlanStore.ClaimRejectReason.None, reason);
    }

    [Fact]
    public async Task Apply_MissingCapability_PlanRemainsRetryable()
    {
        // Аудит 3, п. 6: capability отсутствует — изменения не было, план не
        // помечается применённым и остаётся подтверждённым для повтора.
        var holder = new FakeCapabilityHolder();
        var (plans, executor) = CreateWithCapability(holder.Create("Вкл."));
        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "ghost", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        var outcome = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(ScuAiErrorCode.CapabilityNotFound, outcome.ErrorCode);
        Assert.NotNull(plans.Get(plan.PlanId));
        Assert.True(plans.IsConfirmed(plan.PlanId));
    }

    [Fact]
    public async Task Apply_ReadStateAfterSuccessThrows_StillSuccess()
    {
        // Аудит 3, п. 7: мутация уже прошла — ошибка перечитывания состояния не
        // превращает успешное применение в Failure, только добавляет warning.
        var readCalls = 0;
        var capability = new ScuAiCapability
        {
            UtilityId = "readflaky",
            Title = "Тест",
            Risk = ScuAiRiskLevel.Mutate,
            ReadState = () =>
            {
                readCalls++;
                if (readCalls >= 2)
                {
                    throw new IOException("состояние недоступно");
                }
                return "Вкл.";
            },
            Apply = () => Task.FromResult<(bool, string)>((true, "Применено")),
        };
        var (plans, executor) = CreateWithCapability(capability);
        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "readflaky", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.Mutate,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        var outcome = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Result);
        Assert.Contains("подтвердить новое состояние не удалось", outcome.Message);
        // Мутация прошла и план закрыт как применённый (не вернулся в Confirmed).
        Assert.Null(plans.Get(plan.PlanId));
    }

    [Fact]
    public async Task Apply_HighRiskWithoutDialogs_FailsClosed()
    {
        // Аудит 2, п. 19: HighRisk без Dialogs — отказ, а не молчаливое применение.
        var holder = new FakeCapabilityHolder();
        var capability = holder.Create("Вкл.", ScuAiRiskLevel.HighRisk);
        var (plans, executor) = CreateWithCapability(capability);

        var plan = plans.Create(new ScuAiActionPlanTemplate(
            "prepare_scu_change", "fake", "Тест",
            "Вкл.", "Выкл.", ScuAiRiskLevel.HighRisk,
            "Сводка", "Детали", false, false));
        executor.Confirm(plan.PlanId);

        var outcome = await executor.ApplyAsync(plan.PlanId, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(ScuAiErrorCode.ConfirmationCancelled, outcome.ErrorCode);
        Assert.Equal(0, holder.ApplyCalls);
        // План вернулся в Confirmed (claim освобождён), не «применён».
        Assert.True(plans.IsConfirmed(plan.PlanId));
    }
}
