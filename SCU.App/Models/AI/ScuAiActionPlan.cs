namespace SCU.Models.AI;

// План изменения (п. 9 ТЗ): preview привязан к конкретному PlanId и хэшу
// состояния на момент подготовки. Подтверждение относится к этому плану, а не
// к тексту модели: apply принимает только PlanId, произвольный payload от LLM
// выполнить нельзя (защита от подмены действия).
public sealed record ScuAiActionPlan(
    string PlanId,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    string ToolName,
    string UtilityId,
    string UtilityTitle,
    string CurrentState,
    string DesiredState,
    ScuAiRiskLevel Risk,
    string UserVisibleSummary,
    string DetailedChanges,
    bool RequiresElevation,
    bool RequiresRestart,
    string StateHash);

// Отображаемая в карточке сообщения выжимка плана: денормализована в сообщение,
// чтобы история чата показывала карточку даже после перезапуска приложения
// (живой план живёт в памяти только текущей сессии).
public sealed record ScuAiActionPlanSnapshot(
    string PlanId,
    string UtilityTitle,
    string CurrentState,
    string DesiredState,
    string Risk,
    string Summary,
    bool RequiresRestart);

// Ссылка на внутреннюю справку в ответе AI (п. 17 ТЗ): UI строит её сам из
// валидированных данных, модель не рисует ссылки текстом.
public sealed record ScuAiHelpReference(
    string HelpId,
    int SectionNumber,
    string? UtilityId,
    string Title);
