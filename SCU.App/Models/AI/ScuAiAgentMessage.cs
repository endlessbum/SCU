using System.Text.Json;

namespace SCU.Models.AI;

// Сообщение агентного цикла. Роли — формат API (system | user | assistant | tool):
// tool-сообщения не показываются пользователю как обычный чат, но нужны для
// продолжения текущего agent turn (п. 24 ТЗ). API-ключ сюда не попадает.
public sealed class ScuAiAgentMessage
{
    public string Role { get; set; } = "user";

    public string? Content { get; set; }

    // assistant: вызовы tools из ответа модели.
    public List<ScuAiToolCall>? ToolCalls { get; set; }

    // role=tool: id вызванной функции и её имя.
    public string? ToolCallId { get; set; }

    public string? Name { get; set; }

    public static ScuAiAgentMessage System(string content) => new() { Role = "system", Content = content };

    public static ScuAiAgentMessage User(string content) => new() { Role = "user", Content = content };

    public static ScuAiAgentMessage Assistant(string? content, IReadOnlyList<ScuAiToolCall>? toolCalls) => new()
    {
        Role = "assistant",
        Content = content,
        ToolCalls = toolCalls is { Count: > 0 } ? [.. toolCalls] : null,
    };

    public static ScuAiAgentMessage Tool(string toolCallId, string name, string resultJson) => new()
    {
        Role = "tool",
        ToolCallId = toolCallId,
        Name = name,
        Content = resultJson,
    };

    // Сериализация в payload API: null-поля опускаются, чтобы провайдер не
    // получил пустой content вместе с tool_calls (чаще всего это 400).
    public string ToPayloadJson() => JsonSerializer.Serialize(this, ScuAiMessageSerializer.Options);
}

internal static class ScuAiMessageSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
