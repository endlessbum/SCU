using System.Text.Json;
using System.Windows;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// Help tools: поиск и получение записей внутренней справки SCU (п. 8A ТЗ).
// Для вопросов о функциях SCU модель должна сначала использовать search_scu_help.
internal sealed class ScuSearchHelpTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuSearchHelpTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "search_scu_help";

    public string Description =>
        "Ищет во внутренней справке SCU описание функций, настроек и утилит. " +
        "Используй первым шагом для любых вопросов о возможностях SCU. " +
        "Возвращает релевантные записи: id, section (номер раздела), title, " +
        "description, info, utility_id (если есть связь с конкретной функцией).";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object(
        ("query", "string", "Поисковый запрос пользователя: название функции, синоним, ключевое слово (RU/EN).", true),
        ("max_results", "integer", "Максимум записей в ответе (по умолчанию 5).", false)));

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var query = arguments.TryGetProperty("query", out var queryElement)
            ? queryElement.GetString() ?? string.Empty
            : string.Empty;
        var maxResults = arguments.TryGetProperty("max_results", out var maxElement)
            && maxElement.TryGetInt32(out var max)
                ? Math.Clamp(max, 1, 20)
                : 5;

        var results = _deps.Help.Search(query, maxResults);

        // Ссылки на внутреннюю справку для UI (п. 17 ТЗ): карточки строит
        // приложение из найденных записей, а не модель из текста.
        context.AttachHelp(results.Select(entry => new ScuAiHelpReference(
            entry.Id,
            entry.SectionNumber,
            entry.RelatedUtilityId,
            entry.Title)));

        var payload = new
        {
            query,
            total = results.Count,
            entries = results.Select(entry => new
            {
                id = entry.Id,
                section = entry.SectionNumber,
                section_title = SectionTitle(entry.SectionNumber),
                title = entry.Title,
                description = entry.Description,
                info = entry.InfoText,
                keywords = entry.Keywords,
                utility_id = entry.RelatedUtilityId,
            }),
        };

        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(payload)));
    }

    private static string SectionTitle(int sectionNumber) =>
        Application.Current?.TryFindResource($"S_Section{sectionNumber:00}_Title") as string
        ?? $"S_Section{sectionNumber:00}_Title";
}

internal sealed class ScuGetHelpTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuGetHelpTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "get_scu_help";

    public string Description =>
        "Получить конкретную запись внутренней справки SCU по её id " +
        "(id приходит в результатах search_scu_help).";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object(
        ("id", "string", "id записи справки (например, switch_10_animations, section_09).", true)));

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var id = arguments.TryGetProperty("id", out var idElement)
            ? idElement.GetString() ?? string.Empty
            : string.Empty;

        var entry = _deps.Help.Get(id);
        if (entry is null)
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.ValidationFailed,
                L.T("Запись справки не найдена: {0}", id)));
        }

        var payload = new
        {
            id = entry.Id,
            section = entry.SectionNumber,
            title = entry.Title,
            description = entry.Description,
            info = entry.InfoText,
            keywords = entry.Keywords,
            utility_id = entry.RelatedUtilityId,
        };
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(payload)));
    }
}
