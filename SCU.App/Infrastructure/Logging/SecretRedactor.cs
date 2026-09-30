using System.Text.RegularExpressions;

namespace SCU.Infrastructure.Logging;

// Маскирование секретов в сообщениях журнала (п. 34F.31 ТЗ — «logs redact
// authorization»). Защита «в глубину»: API-ключи передаются только в заголовке
// HTTP-запроса и не должны оказываться в сообщениях вообще, но любая случайная
// утечка (диагностика провайдера, вывод скрипта, чужая строка) не должна
// раскрыть секрет в файле лога.
//
// Маскируются только известные форматы авторизационных токенов:
// - заголовок Authorization: Bearer <токен>;
// - DeepAI/OpenAI-стиль ключей: sk-<длинный токен>;
// - Assignment ключей в выводе: api_key=<...>, apikey: <...>.
// Остальной текст не трогается — лог остаётся читаемым для диагностики.
internal static class SecretRedactor
{
    private const string Redacted = "[REDACTED]";

    // Bearer <token> — заголовок авторизации в HTTP-запросах к AI-провайдерам.
    private static readonly Regex BearerPattern =
        new(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9\-_.~+/=]{6,}", RegexOptions.Compiled);

    // sk-<token> — ключи в стиле OpenAI/DeepSeek.
    private static readonly Regex SkTokenPattern =
        new(@"\bsk-[A-Za-z0-9\-_]{6,}", RegexOptions.Compiled);

    // Явная передача ключа параметром/присваиванием в выводе команд.
    private static readonly Regex AssignmentPattern =
        new(@"(?i)(api[_-]?key|token|authorization)(\s*[:=]\s*)([""']?)([A-Za-z0-9\-_.]{8,})\3",
            RegexOptions.Compiled);

    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }

        if (!CouldContainSecret(message))
        {
            return message;
        }

        message = BearerPattern.Replace(message, match =>
            match.Value.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
                ? "Bearer " + Redacted
                : "Basic " + Redacted);
        message = SkTokenPattern.Replace(message, "sk-" + Redacted);
        message = AssignmentPattern.Replace(message, Evaluation);
        return message;
    }

    // Оставляем «api_key = » и тип значения, маскируем только сам секрет.
    private static string Evaluation(Match match) =>
        match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value + Redacted;

    // Быстрая проверка до запуска регулярок: маскирование включается только для
    // строк, содержащих один из маркеров секрета.
    private static bool CouldContainSecret(string message) =>
        message.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Basic ", StringComparison.OrdinalIgnoreCase)
        || message.Contains("sk-", StringComparison.Ordinal)
        || message.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || message.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || message.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || message.Contains("token", StringComparison.OrdinalIgnoreCase);
}
