using System.Reflection;
using SCU.Infrastructure.DeepSeek;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// П. 34E ТЗ:
// - 23: обычный чат не сломан —.SendChatAsync остаётся в прежнем контракте;
// - 24: agent flow разбирает tool_calls из OpenAI-compatible ответа.
public class ScuAiClientParsingTests
{
    private const string ToolCallsBody =
        """
        {
          "id": "chatcmpl-1",
          "object": "chat.completion",
          "model": "deepseek-chat",
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": null,
                "tool_calls": [
                  {
                    "id": "call_a1",
                    "type": "function",
                    "function": {
                      "name": "open_scu_section",
                      "arguments": "{\"section\":8}"
                    }
                  },
                  {
                    "id": "call_a2",
                    "type": "function",
                    "function": {
                      "name": "prepare_scu_change",
                      "arguments": "{\"utility_id\":\"visual-animations\",\"desired_state\":\"off\"}"
                    }
                  }
                ]
              },
              "finish_reason": "tool_calls"
            }
          ],
          "usage": { "prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15 }
        }
        """;

    private const string TextOnlyBody =
        """
        {
          "id": "chatcmpl-2",
          "object": "chat.completion",
          "model": "deepseek-chat",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "Анимации отключаются в разделе «Интерфейс»." },
              "finish_reason": "stop"
            }
          ],
          "usage": { "total_tokens": 42 }
        }
        """;

    [Fact]
    public void ParseAgentCompletion_ToolCalls_Parsed()
    {
        // П. 34E.24: оба вызова разобраны — id (нужен для возврата результата),
        // имя функции и сырые аргументы (валидируются приложением, а не здесь).
        var (completion, usageTokens) = DeepSeekClient.ParseAgentCompletion(ToolCallsBody, "deepseek-chat");

        Assert.True(completion.IsSuccess);
        Assert.Equal(15, usageTokens);
        Assert.Equal("tool_calls", completion.FinishReason);
        Assert.Equal(2, completion.ToolCalls.Count);

        var first = completion.ToolCalls[0];
        Assert.Equal("call_a1", first.Id);
        Assert.Equal("open_scu_section", first.Name);
        Assert.Contains("\"section\":8", first.ArgumentsJson);
        Assert.Contains("visual-animations", completion.ToolCalls[1].ArgumentsJson);
    }

    [Fact]
    public void ParseAgentCompletion_TextOnly_HasNoToolCalls()
    {
        var (completion, _) = DeepSeekClient.ParseAgentCompletion(TextOnlyBody, "deepseek-chat");

        Assert.True(completion.IsSuccess);
        Assert.False(completion.HasToolCalls);
        Assert.Equal("Анимации отключаются в разделе «Интерфейс».", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
    }

    [Fact]
    public void PlainChat_StillsUsesLegacySendChatContract()
    {
        // П. 34E.23/п. 27 ТЗ: обычный чат остался на прежнем публичном
        // контракте — метод не переименован, не убран и возвращает ChatReply.
        var method = typeof(DeepSeekClient).GetMethod(
            "SendChatAsync",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<>).MakeGenericType(typeof(DeepSeekClient.ChatReply)), method!.ReturnType);
    }

    [Fact]
    public void AgentPayload_Serialized_InOpenAiWireFormat()
    {
        // Регрессия: payload агентного шага уходит в API в формате OpenAI —
        // snake_case-ключи, null-поля опущены. Дефолтная сериализация давала
        // PascalCase («Model», «Messages», «ToolCallId») и «Tools»: null —
        // Cloudflare отвечал 400 «не принял запрос», обычный чат при этом
        // работал, поэтому рассинхрон не был виден до живого запроса.
        var payload = DeepSeekClient.BuildAgentPayload(
            "test-model",
            4096,
            "Ты — помощник SCU.",
            [
                ScuAiAgentMessage.User("привет"),
                ScuAiAgentMessage.Assistant(
                    string.Empty,
                    [new ScuAiToolCall("call_1", "get_scu_context", "{}")]),
                ScuAiAgentMessage.Tool("call_1", "get_scu_context", "{\"success\":true}"),
            ],
            tools: null);

        var body = System.Text.Json.JsonSerializer.Serialize(payload, DeepSeekClient.AgentJsonOptions);

        Assert.Contains("\"model\":\"test-model\"", body);
        Assert.Contains("\"max_tokens\":4096", body);
        Assert.Contains("\"stream\":false", body);
        Assert.Contains("\"role\":\"user\"", body);
        Assert.Contains("\"role\":\"tool\"", body);
        Assert.Contains("\"tool_call_id\":\"call_1\"", body);
        Assert.Contains("\"tool_calls\":[", body);
        Assert.Contains("\"function\":{\"name\":\"get_scu_context\"", body);

        // PascalCase-ключей быть не должно.
        Assert.DoesNotContain("Model", body);
        Assert.DoesNotContain("Messages", body);
        Assert.DoesNotContain("ToolCallId", body);
        // Tools: null не сериализуется (tools нет — поле отсутствует).
        Assert.DoesNotContain("\"tools\"", body);
        // У assistant-сообщения с tool_calls пустой content не уходит (400 провайдера).
        Assert.DoesNotContain("\"content\":\"\"", body);
    }
}
