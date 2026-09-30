using System.Security.Cryptography;
using System.Text;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// In-memory store подготовленных планов (п. 9 ТЗ). План:
// - создаётся на этапе prepare (мутация НЕ выполняется);
// - применяется только по точному PlanId;
// - одноразовый: повторный apply того же PlanId невозможен;
// - протухает: к моменту apply состояние должно совпасть с хэшем из preview,
//   иначе — перевалидация и отказ (защита от рассинхрона preview/apply).
public sealed class ScuAiActionPlanStore
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _plansLock = new();
    private readonly Dictionary<string, ScuAiActionPlan> _plans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private readonly Logger _logger;

    public ScuAiActionPlanStore(Logger logger)
    {
        _logger = logger;
    }

    public ScuAiActionPlan Create(ScuAiActionPlanTemplate template) =>
        Create(template, DateTime.Now, DateTime.Now + PlanLifetime);

    // Только тесты: план с явно заданными моментами создания и истечения —
    // проверка протухания (п. 34D.20) без ожидания в реальном времени.
    internal ScuAiActionPlan Create(ScuAiActionPlanTemplate template, DateTime createdAt, DateTime expiresAt)
    {
        var plan = new ScuAiActionPlan(
            PlanId: "plan_" + Guid.NewGuid().ToString("N"),
            CreatedAt: createdAt,
            ExpiresAt: expiresAt,
            ToolName: template.ToolName,
            UtilityId: template.UtilityId,
            UtilityTitle: template.UtilityTitle,
            CurrentState: template.CurrentState,
            DesiredState: template.DesiredState,
            Risk: template.Risk,
            UserVisibleSummary: template.UserVisibleSummary,
            DetailedChanges: template.DetailedChanges,
            RequiresElevation: template.RequiresElevation,
            RequiresRestart: template.RequiresRestart,
            StateHash: ComputeHash(template.UtilityId, template.CurrentState));

        lock (_plansLock)
        {
            _plans[plan.PlanId] = plan;
        }

        _logger.Info($"SCU_AI | action_plan | utility={template.UtilityId} | risk={template.Risk}");
        return plan;
    }

    public ScuAiActionPlan? Get(string planId)
    {
        lock (_plansLock)
        {
            return _plans.TryGetValue(planId, out var plan) ? plan : null;
        }
    }

    // Пользователь нажал «Применить» в карточке подтверждения. Без этого
    // apply_scu_change, вызванный моделью, не выполнится — подтверждение
    // относится к конкретному плану, а не к тексту модели (п. 9 ТЗ).
    public bool Confirm(string planId)
    {
        lock (_plansLock)
        {
            if (!_plans.ContainsKey(planId))
            {
                _logger.Warn($"SCU_AI | confirmation rejected | unknown plan {planId}");
                return false;
            }

            _confirmed.Add(planId);
        }

        _logger.Info($"SCU_AI | confirmation | accepted plan {planId}");
        return true;
    }

    // Подтверждал ли пользователь этот план (п. 9.5: применяется только
    // подтверждённое, причём именно это конкретное действие).
    public bool IsConfirmed(string planId)
    {
        lock (_plansLock)
        {
            return _confirmed.Contains(planId);
        }
    }

    // Заявка на применение: проверка жизни плана, подтверждения, состояния и
    // отсутствия повторного использования. Возвращает план для исполнения.
    public ScuAiActionPlan? TryClaimForApply(string planId, string currentState)
    {
        lock (_plansLock)
        {
            if (!_plans.TryGetValue(planId, out var plan))
            {
                _logger.Warn($"SCU_AI | apply rejected | unknown plan {planId}");
                return null;
            }

            if (_applied.Contains(planId))
            {
                _logger.Warn($"SCU_AI | apply rejected | plan {planId} already applied");
                return null;
            }

            // Не подтверждён пользователем — модель не может применить план сама.
            if (!_confirmed.Contains(planId))
            {
                _logger.Warn($"SCU_AI | apply rejected | plan {planId} not confirmed");
                return null;
            }

            if (DateTime.Now > plan.ExpiresAt)
            {
                _plans.Remove(planId);
                _logger.Warn($"SCU_AI | apply rejected | plan {planId} expired");
                return null;
            }

            // Состояние изменилось после preview — не применяем вслепую (п. 9).
            var currentHash = ComputeHash(plan.UtilityId, currentState);
            if (!string.Equals(currentHash, plan.StateHash, StringComparison.Ordinal))
            {
                _logger.Warn($"SCU_AI | apply rejected | state mismatch for {plan.UtilityId}");
                return null;
            }

            return plan;
        }
    }

    // План исполнен (или отменён пользователем) — больше не применяется.
    public void MarkApplied(string planId)
    {
        lock (_plansLock)
        {
            _applied.Add(planId);
            _plans.Remove(planId);
        }
    }

    public void Cancel(string planId)
    {
        lock (_plansLock)
        {
            _plans.Remove(planId);
            // Подтверждение относится к конкретному плану: отменённый план больше
            // не считается подтверждённым (защита от «старого» подтверждения).
            _confirmed.Remove(planId);
        }

        _logger.Info($"SCU_AI | confirmation | plan {planId} cancelled");
    }

    // Хэш состояния: PlanId + текущее состояние — чтобы заметить любое изменение
    // между preview и apply.
    private static string ComputeHash(string utilityId, string state)
    {
        var bytes = Encoding.UTF8.GetBytes(utilityId + "|" + state);
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }
}

// Шаблон плана: всё, что нужно для preview, без вычисленных полей хранилища.
public sealed record ScuAiActionPlanTemplate(
    string ToolName,
    string UtilityId,
    string UtilityTitle,
    string CurrentState,
    string DesiredState,
    ScuAiRiskLevel Risk,
    string UserVisibleSummary,
    string DetailedChanges,
    bool RequiresElevation,
    bool RequiresRestart);
