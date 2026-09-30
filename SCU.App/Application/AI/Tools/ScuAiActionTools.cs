using System.Text.Json;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// Action/Change tools (п. 8D ТЗ). Мутации идут строго через preview →
// подтверждение пользователя → apply по PlanId (п. 9 ТЗ): выполнить
// произвольный payload от модели нельзя.

// 17) prepare_scu_change: формирует валидированный preview, ничего не меняя.
internal sealed class ScuPrepareChangeTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuPrepareChangeTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "prepare_scu_change";

    public string Description =>
        "Готовит изменение настройки SCU: проверяет функцию по реестру утилит, " +
        "читает текущее состояние и возвращает preview для подтверждения " +
        "(текущее значение, целевое, риск, нужна ли перезагрузка). " +
        "Система при этом НЕ меняется. После подтверждения пользователем вызывай " +
        "apply_scu_change с возвращённым action_plan_id.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object(
        ("utility_id", "string", ScuAiSchemaBuilder.UtilityIdDescription, true),
        ("desired_state", "string", ScuAiSchemaBuilder.DesiredStateDescription, false)));

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var utilityId = arguments.TryGetProperty("utility_id", out var idElement)
            ? idElement.GetString() ?? string.Empty
            : string.Empty;

        var capability = _deps.Capabilities.Find(utilityId);
        if (capability is null)
        {
            _deps.Logger.Warn($"SCU_AI | prepare rejected | unknown utility {utilityId}");
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.CapabilityNotFound,
                L.T("Неизвестный id функции SCU: {0}", utilityId)));
        }

        var currentState = capability.ReadState();
        var desiredState = ResolveDesiredState(capability, arguments);

        // desired_state от модели должен совпадать с реальным направлением
        // функции SCU: SCU применяет тумблер в одну сторону (например, только
        // отключает анимации). Расхождение — отказ, а не подмена действия.
        if (desiredState.ViolationMessage is { Length: > 0 } violation)
        {
            _deps.Logger.Warn($"SCU_AI | prepare rejected | {violation}");
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.ValidationFailed, L.T(violation)));
        }

        // Целевое состояние уже достигнуто — менять нечего.
        if (!capability.IsOperation && string.Equals(currentState, desiredState.Text, StringComparison.Ordinal))
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.ValidationFailed,
                L.T("Функция уже находится в запрошенном состоянии — менять нечего.")));
        }

        var plan = _deps.Plans.Create(new ScuAiActionPlanTemplate(
            ToolName: Name,
            UtilityId: capability.UtilityId,
            UtilityTitle: capability.Title,
            CurrentState: currentState,
            DesiredState: desiredState.Text,
            Risk: capability.Risk,
            UserVisibleSummary: L.T(
                "Изменение: {0} — {1} → {2}.", capability.Title, currentState, desiredState.Text),
            DetailedChanges: L.T(
                "Функция SCU «{0}» будет применена через существующую команду раздела {1}.",
                capability.Title, capability.Section),
            RequiresElevation: capability.RequiresElevation,
            RequiresRestart: capability.RequiresRestart));

        // Снапшот для UI: что увидит пользователь в карточке подтверждения.
        context.AttachPlan(new ScuAiActionPlanSnapshot(
            plan.PlanId,
            capability.Title,
            currentState,
            desiredState.Text,
            capability.Risk.ToString(),
            plan.UserVisibleSummary,
            capability.RequiresRestart));

        return Task.FromResult(ScuAiToolResult.Confirmation(plan.PlanId, JsonSerializer.Serialize(new
        {
            action_plan_id = plan.PlanId,
            utility_id = capability.UtilityId,
            title = capability.Title,
            current = currentState,
            desired = desiredState.Text,
            risk = capability.Risk.ToString(),
            requires_restart = capability.RequiresRestart,
            requires_elevation = capability.RequiresElevation,
            expires_at = plan.ExpiresAt.ToString("HH:mm"),
        })));
    }

    // Целевое состояние: для операций-кнопок — «применить операцию», для
    // тумблеров — целевое состояние функции SCU; желаемое модели должно с ним
    // совпадать, иначе план не создаётся.
    private static (string Text, string? ViolationMessage, bool IsOperation) ResolveDesiredState(
        ScuAiCapability capability, JsonElement arguments)
    {
        if (capability.IsOperation)
        {
            return (L.T("Применить операцию"), null, true);
        }

        if (arguments.TryGetProperty("desired_state", out var stateElement)
            && stateElement.ValueKind == JsonValueKind.String)
        {
            // Направление запроса модели: on — включить, off — выключить.
            var requested = stateElement.GetString();
            var requestedOn = requested switch
            {
                "on" => (bool?)true,
                "off" => (bool?)false,
                _ => null,
            };

            if (requestedOn is null)
            {
                return (capability.DesiredState ?? string.Empty, L.T(
                    "Неизвестное желаемое состояние: {0}. Допустимы только «on» и «off».", requested), false);
            }

            // Сколько бы ни просила модель, SCU применяет тумблер в одну сторону:
            // направление запроса должно совпадать с направлением capability.
            // Расхождение — отказ, а не подмена действия (п. 8D ТЗ).
            if (requestedOn != capability.DesiredStateIsOn)
            {
                return (capability.DesiredState ?? string.Empty, L.T(
                    "Функция SCU «{0}» применяется только в состоянии «{1}» — противоположное направление недоступно.",
                    capability.Title, capability.DesiredState), false);
            }
        }

        return (capability.DesiredState ?? string.Empty, null, capability.IsOperation);
    }
}

// 18) apply_scu_change: выполнение подтверждённого плана.
internal sealed class ScuApplyChangeTool : IScuAiTool
{
    private readonly ScuAiPlanExecutor _executor;

    public ScuApplyChangeTool(ScuAiPlanExecutor executor) => _executor = executor;

    public string Name => "apply_scu_change";

    public string Description =>
        "Применяет ранее подготовленное и подтверждённое изменение. " +
        "action_plan_id берётся из результата prepare_scu_change; выполнить " +
        "можно только подтверждённый пользователем план, один раз. " +
        "Не передавай аргументы операции напрямую — только action_plan_id.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object(
        ("action_plan_id", "string", "id подготовленного плана из prepare_scu_change.", true)));

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.Mutate;

    public async Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var planId = arguments.TryGetProperty("action_plan_id", out var idElement)
            ? idElement.GetString() ?? string.Empty
            : string.Empty;

        // Вся проверка и выполнение — в общем исполнителе (его же использует
        // кнопка «Применить» в карточке подтверждения).
        var outcome = await _executor.ApplyAsync(planId, context.CancellationToken).ConfigureAwait(true);
        if (!outcome.Success)
        {
            return ScuAiToolResult.Failure(outcome.ErrorCode ?? ScuAiErrorCode.Internal, outcome.Message);
        }

        if (outcome.Result is { } result)
        {
            context.AttachResult(result);

            return ScuAiToolResult.Applied(JsonSerializer.Serialize(new
            {
                utility_id = result.UtilityTitle,
                before = result.BeforeState,
                after = result.AfterState,
                status = result.Status,
            }));
        }

        return ScuAiToolResult.Failure(ScuAiErrorCode.Internal, L.T("Внутренняя ошибка выполнения tool."));
    }
}

// Результат применения для карточки в чате: было/стало/статус раздела.
public sealed record ScuAiActionResultSnapshot(
    string PlanId, string UtilityTitle, string BeforeState, string AfterState, string Status);
