using System.Text.Json;

namespace SCU.Models.AI;

// Один вызов tool из ответа модели: Id — tool_call_id провайдера
// (нужен для возврата результата обратно в message history), ArgumentsJson —
// сырой JSON аргументов; валидируется приложением, а не моделью.
public sealed record ScuAiToolCall(string Id, string Name, string ArgumentsJson);

// Описание tool для отправки в API: имя, описание и JSON-схема параметров
// (OpenAI-compatible function calling).
public sealed record ScuAiToolSchema(string Name, string Description, string ParametersJson);

// Результат tool (п. 13 ТЗ): либо успех с данными, либо структурированная
// ошибка; для мутаций — требует подтверждения и несёт ActionPlanId.
public sealed class ScuAiToolResult
{
    public bool Success { get; init; }
    public string? DataJson { get; init; }
    public ScuAiErrorCode? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool Changed { get; init; }
    public bool RequiresConfirmation { get; init; }
    public string? ActionPlanId { get; init; }

    // Read-only: данные для модели.
    public static ScuAiToolResult Ok(string? dataJson = null) => new() { Success = true, DataJson = dataJson };

    // Подготовлена мутация: preview создан, ждёт подтверждения пользователя.
    public static ScuAiToolResult Confirmation(string actionPlanId, string dataJson) => new()
    {
        Success = true,
        DataJson = dataJson,
        RequiresConfirmation = true,
        ActionPlanId = actionPlanId,
    };

    // Мутация применена: модель получает реальные before/after.
    public static ScuAiToolResult Applied(string dataJson) => new()
    {
        Success = true,
        DataJson = dataJson,
        Changed = true,
    };

    // Ошибка: never fake success (п. 15 ТЗ).
    public static ScuAiToolResult Failure(ScuAiErrorCode code, string message) => new()
    {
        ErrorCode = code,
        ErrorMessage = message,
    };

    public string ToModelJson()
    {
        if (!Success)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error_code = ErrorCode?.ToString() ?? ScuAiErrorCode.Internal.ToString(),
                error = ErrorMessage ?? string.Empty,
            });
        }

        if (RequiresConfirmation)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                requires_confirmation = true,
                action_plan_id = ActionPlanId,
                data = DataJson is { Length: > 0 } ? (JsonElement?)JsonDocument.Parse(DataJson).RootElement : null,
            });
        }

        return DataJson is { Length: > 0 }
            ? JsonSerializer.Serialize(new { success = true, changed = Changed, data = (JsonElement?)JsonDocument.Parse(DataJson).RootElement })
            : JsonSerializer.Serialize(new { success = true, changed = Changed });
    }
}
