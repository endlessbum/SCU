using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SCU.Models.AI;

// Тип сообщения в чате SCU AI Assistant (п. 31 ТЗ): UI рендерит карточки по
// этому признаку, а не по тексту модели — LLM не может нарисовать кнопку или
// ссылку текстом (п. 32 ТЗ).
public enum ScuAiMessageKind
{
    // Обычный текст пользователя/ассистента.
    Text,

    // Ответ содержит ссылки на внутреннюю справку (п. 17 ТЗ).
    HelpReference,

    // Карточка подготовленного изменения с кнопками «Отмена»/«Применить» (п. 19).
    ActionPlan,

    // Результат применённой операции: было/стало/статус раздела.
    ActionResult,

    // Структурированная ошибка tool: модель не маскирует её под успех (п. 33).
    ToolError,
}

// Сообщение чата SCU AI Assistant. Текстовые сообщения уходят в API; карточки
// (HelpReference/ActionPlan/ActionResult/ToolError) — локальные UI-события,
// в API не отправляются (п. 24 ТЗ: tool messages живут в контексте выполнения).
public sealed partial class ScuAiChatMessage : ObservableObject
{
    public ScuAiChatMessage(string role, string content, ScuAiMessageKind kind)
    {
        Role = role;
        Content = content;
        Kind = kind;
        Timestamp = DateTime.Now;
    }

    // "user" | "assistant" — формат API.
    public string Role { get; }

    // Текст сообщения; у карточки ActionPlan — краткое описание изменения.
    public string Content { get; set; }

    public ScuAiMessageKind Kind { get; }

    public DateTime Timestamp { get; }

    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    public bool IsError => Kind == ScuAiMessageKind.ToolError;

    // Ссылки на внутреннюю справку (Kind == HelpReference): UI строит их сам из
    // валидированных записей, модель их не придумывает.
    public ObservableCollection<ScuAiHelpReference> HelpReferences { get; } = [];

    // Снапшот плана для карточки подтверждения (Kind == ActionPlan).
    public ScuAiActionPlanSnapshot? ActionPlan { get; set; }

    // Исход карточки подтверждения: после применения/отмены кнопки скрываются —
    // план одноразовый (п. 9 ТЗ), повторное нажатие ничего не сделает.
    [ObservableProperty]
    private bool _isPlanApplied;

    [ObservableProperty]
    private bool _isPlanCancelled;

    // Было/стало для Kind == ActionResult.
    public string? BeforeState { get; set; }

    public string? AfterState { get; set; }

    public string TimeText => Timestamp.ToString("HH:mm");
}
