using System.Text.Json;
using SCU.Common;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// Исполнение подтверждённого плана (п. 9.6-9.8 ТЗ) — общий путь для apply_scu_change
// (модель) и для кнопки «Применить» в карточке подтверждения (UI). Подтверждение
// относится к конкретному PlanId: выполнить произвольный payload от модели нельзя.
//
// Проверки идут в том же порядке, что и в tool: жизнь плана → подтверждение
// пользователя → наличие capability → повторное чтение состояния → однократный
// claim → существующая команда SCU → перечитывание результата.
public sealed class ScuAiPlanExecutor
{
    private readonly ScuAiToolDeps _deps;

    public ScuAiPlanExecutor(ScuAiToolDeps deps) => _deps = deps;

    // Исход применения: успех — с карточкой результата для чата; неудача —
    // локализованной причиной (она же уходит в лог, стек модели не отдаём).
    public sealed record ApplyOutcome(
        bool Success,
        ScuAiErrorCode? ErrorCode,
        string Message,
        ScuAiActionResultSnapshot? Result)
    {
        public static ApplyOutcome Fail(ScuAiErrorCode code, string message) =>
            new(false, code, message, null);
    }

    // Пользователь нажал «Применить» в карточке подтверждения (п. 9.5):
    // подтверждение относится к конкретному PlanId — модель не может
    // применить план, который пользователь не подтвердил.
    public bool Confirm(string planId) => _deps.Plans.Confirm(planId);

    // Пользователь отказался от изменения: план больше не валиден.
    public bool Cancel(string planId) => _deps.Plans.Cancel(planId);

    public async Task<ApplyOutcome> ApplyAsync(string planId, CancellationToken cancellationToken)
    {
        var probe = _deps.Plans.Get(planId);
        if (probe is null)
        {
            _deps.Logger.Warn($"SCU_AI | apply rejected | unknown plan {planId}");
            return ApplyOutcome.Fail(ScuAiErrorCode.PlanExpired,
                L.T("План изменения не найден или истёк. Подготовьте его заново."));
        }

        // Подтверждение приходит от UI, а не от модели: применить неподтверждённый
        // план невозможно (п. 9.5 ТЗ).
        if (!_deps.Plans.IsConfirmed(planId))
        {
            _deps.Logger.Warn($"SCU_AI | apply rejected | plan {planId} not confirmed");
            return ApplyOutcome.Fail(ScuAiErrorCode.ConfirmationCancelled,
                L.T("Изменение не подтверждено пользователем."));
        }

        var capability = _deps.Capabilities.Find(probe.UtilityId);
        if (capability is null)
        {
            // Аудит 3, п. 6: capability отсутствует — изменения НЕ было, план не
            // может считаться применённым. Claim не освобождён ещё, но и не взят:
            // план остаётся подтверждённым для повтора (до истечения срока).
            _deps.Plans.MarkApplyFailed(planId);
            _deps.Logger.Warn($"SCU_AI | apply rejected | capability missing | {probe.UtilityId}");
            return ApplyOutcome.Fail(ScuAiErrorCode.CapabilityNotFound,
                L.T("Функция SCU больше не зарегистрирована: {0}", probe.UtilityId));
        }

        // Состояние могло измениться после preview: применяем только то, что
        // пользователь видел в карточке (п. 9 ТЗ — защита от рассинхрона).
        var before = capability.ReadState();
        var plan = _deps.Plans.TryClaimForApply(planId, before, out var claimReason);
        if (plan is null)
        {
            return ApplyOutcome.Fail(ScuAiErrorCode.StateMismatch,
                claimReason == ScuAiActionPlanStore.ClaimRejectReason.AlreadyApplying
                    ? L.T("Изменение уже применяется. Дождитесь завершения.")
                    : L.T("Состояние изменилось после подготовки preview. Подготовьте изменение заново."));
        }

        // После claim план в состоянии Applying: ЛЮБОЙ выход из apply (отмена,
        // исключение capability) обязан вернуть план в Confirmed — иначе PlanId
        // навечно блокируется с AlreadyApplying (аудит 3, п. 3/4).
        try
        {
            // П. 3/7 ARCH rules + п. 42.11: HighRisk-операция требует отдельного
            // явного подтверждения сверх нажатия «Применить» — тот же существующий
            // флоу DestructiveChange, что и у опасных операций SCU.
            // Fail-closed (аудит 2, п. 19): Dialogs == null для HighRisk — отказ,
            // а не молчаливое продолжение без второго подтверждения.
            if (capability.Risk == ScuAiRiskLevel.HighRisk)
            {
                if (_deps.Dialogs is not { } dialogs)
                {
                    _deps.Plans.MarkApplyFailed(planId);
                    _deps.Logger.Error($"SCU_AI | apply rejected | high-risk plan {planId} without dialogs dependency");
                    return ApplyOutcome.Fail(ScuAiErrorCode.ConfirmationCancelled,
                        L.T("Опасная операция требует подтверждения, но диалог недоступен — система не изменена."));
                }

                if (!dialogs.ConfirmChange(new DestructiveChange(
                        Title: L.T("Подтвердите опасное изменение"),
                        CurrentState: before,
                        NewState: plan.DesiredState,
                        Consequences: plan.DetailedChanges,
                        Rollback: L.T("через точку восстановления или резервную копию раздела"),
                        ConfirmText: L.T("Применить"))))
                {
                    _deps.Plans.MarkApplyFailed(planId);
                    _deps.Logger.Warn($"SCU_AI | apply rejected | high-risk plan {planId} declined");
                    return ApplyOutcome.Fail(ScuAiErrorCode.ConfirmationCancelled,
                        L.T("Опасная операция не подтверждена — система не изменилась."));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            var (success, message) = await capability.Apply().ConfigureAwait(true);
            if (!success)
            {
                // Never fake success (п. 15 ТЗ): план не отмечается применённым,
                // кнопка остаётся доступной для повтора. Claim освобождается,
                // чтобы повторный apply того же плана был возможен (аудит 2, п. 18).
                _deps.Plans.MarkApplyFailed(planId);
                return ApplyOutcome.Fail(ScuAiErrorCode.ExecutionFailed, message);
            }

            _deps.Plans.MarkApplied(planId);

            // После выполнения состояние перечитывается тем же источником (п. 9.8).
            // Аудит 3, п. 7: успешная мутация уже произошла — ошибка перечитывания
            // НЕ превращает операцию в Failure, только помечает результат warning'ом.
            string after;
            try
            {
                after = capability.ReadState();
            }
            catch (Exception readException)
            {
                _deps.Logger.Warn($"SCU_AI | post-state read failed | {readException.Message}");
                after = L.T("Состояние недоступно для подтверждения");
                message = L.T("{0} Изменение применено, но подтвердить новое состояние не удалось.", message);
            }

            var result = new ScuAiActionResultSnapshot(planId, capability.Title, before, after, message);
            return new ApplyOutcome(true, null, message, result);
        }
        catch (OperationCanceledException)
        {
            // Отмена после claim: мутация не выполнялась/не завершилась — план
            // возвращается в Confirmed, повторный apply возможен.
            _deps.Plans.MarkApplyFailed(planId);
            throw;
        }
        catch (Exception exception)
        {
            // Исключение capability (которое сверху гасит ScuAiToolRegistry/UI)
            // не должно оставлять план в Applying навечно.
            _deps.Plans.MarkApplyFailed(planId);
            _deps.Logger.Error("SCU_AI | apply threw | " + exception);
            throw;
        }
    }
}
