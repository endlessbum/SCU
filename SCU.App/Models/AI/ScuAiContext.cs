namespace SCU.Models.AI;

// Минимальный безопасный контекст приложения для AI (п. 11 ТЗ). Собирается
// ContextBuilder'ом — API-ключ, настройки, токены и пути пользователя сюда
// НИКОГДА не попадают.
public sealed record ScuAiContext(
    string Application,
    string Version,
    string Language,
    bool IsAdmin,
    int? CurrentSectionNumber,
    string CurrentSectionTitle,
    string? CurrentUtilityId,
    string? CurrentUtilityTitle,
    string ProviderId,
    string Model,
    bool SupportsToolCalling);
