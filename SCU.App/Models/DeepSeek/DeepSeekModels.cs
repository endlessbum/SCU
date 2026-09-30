using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SCU.Models.DeepSeek;

// Сообщение переписки: role — "user" | "assistant" | "system" (формат API DeepSeek).
// Error — флаг локальной записи об ошибке запроса: в API такие сообщения не уходят.
public sealed class DeepSeekChatMessage
{
    public string Role { get; set; } = "user";

    public string Content { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;

    [JsonIgnore]
    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsError { get; set; }
}

// Один чат истории: заголовок — первые слова первого вопроса пользователя.
public sealed class DeepSeekChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<DeepSeekChatMessage> Messages { get; set; } = [];
}
