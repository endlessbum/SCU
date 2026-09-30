using System.Text.Json.Nodes;

namespace SCU.AppCore.AI;

// Построение JSON-схем параметров tool (OpenAI-compatible function calling)
// в виде строк — компактно, без лишних DTO под каждый tool.
internal static class ScuAiSchemaBuilder
{
    // Объект с типизированными свойствами: (имя, тип, описание, обязательно).
    internal static string Object(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var schema = new JsonObject { ["type"] = "object" };
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in properties)
        {
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (isRequired)
            {
                required.Add(name);
            }
        }

        schema["properties"] = props;
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema.ToJsonString();
    }

    // Свойство-строка с перечислимыми значениями.
    internal static string Enum(string name, string description, string[] values, bool required = false)
    {
        var schema = new JsonObject { ["type"] = "object" };
        var props = new JsonObject
        {
            [name] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["enum"] = new JsonArray(values.Select(value => (JsonNode)value).ToArray()),
            },
        };
        schema["properties"] = props;
        if (required)
        {
            schema["required"] = new JsonArray(name);
        }

        return schema.ToJsonString();
    }

    internal const string UtilityIdDescription =
        "Идентификатор функции SCU (utility_id) из результата search_scu_help " +
        "(например, row_animations, privacy_telemetry, clean_temp).";

    internal const string DesiredStateDescription =
        "Желаемое состояние для тумблерных настроек: \"on\" (включить) или \"off\" (выключить). " +
        "Не указывайте для операций-кнопок (очистка, проверка, бэкап).";
}
