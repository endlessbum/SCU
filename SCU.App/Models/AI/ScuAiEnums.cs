namespace SCU.Models.AI;

// Уровень риска tool: определяется кодом регистрации, никогда — моделью.
// ReadOnly — выполнение без подтверждения; Navigate — переключение раздела
// (систему не меняет); Mutate — preview + подтверждение пользователя;
// HighRisk — отдельное явное подтверждение через IConfirmDialogService.
public enum ScuAiRiskLevel
{
    ReadOnly,
    Navigate,
    Mutate,
    HighRisk,
}

// Классификация намерения локальным scope guard (п. 15 ТЗ). Не единственная
// граница безопасности: registry + валидация аргументов действуют всегда.
public enum ScuAiIntent
{
    Help,
    ReadState,
    Navigate,
    ChangeSetting,
    Diagnose,
    Unsupported,
    Unknown,
}

// Категории ошибок tool/API разделены (п. 33 ТЗ): модель не должна маскировать
// одну ошибку другой и выдавать сбой за успех.
public enum ScuAiErrorCode
{
    UnknownTool,
    InvalidArguments,
    ValidationFailed,
    CapabilityNotFound,
    StateMismatch,
    PlanExpired,
    PlanAlreadyApplied,
    ConfirmationCancelled,
    PermissionDenied,
    ExecutionFailed,
    ProviderUnsupported,
    ApiError,
    Timeout,
    Cancelled,
    IterationLimit,
    Internal,
}
