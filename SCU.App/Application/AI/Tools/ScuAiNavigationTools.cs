using System.Text.Json;
using System.Windows;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// Navigation tools (п. 8B ТЗ): переключение раздела и фокус утилиты — через
// существующий механизм MainViewModel.SelectSectionByNumber + SectionHighlight
// (п. 30 ТЗ: новый navigation host не создаётся).
internal sealed class ScuOpenSectionTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuOpenSectionTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "open_scu_section";

    // Описание собирается из реальных разделов SCU — модель не догадывается о
    // номерах, а получает их в описании tool.
    public string Description =>
        "Открывает раздел SCU по его номеру. Доступные разделы: " +
        string.Join(", ", _deps.Environment.AvailableSections.Select(FormatSection)) + ".";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object(
        ("section", "integer", "Номер раздела SCU для открытия.", true)));

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.Navigate;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        if (!arguments.TryGetProperty("section", out var sectionElement)
            || !sectionElement.TryGetInt32(out var section))
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.InvalidArguments,
                L.T("Не указан номер раздела.")));
        }

        if (!_deps.Environment.AvailableSections.Contains(section))
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.ValidationFailed,
                L.T("Раздел {0} не существует в SCU.", section)));
        }

        var opened = _deps.Environment.NavigateToSection(section);
        var title = _deps.Environment.CurrentSectionTitle;
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            section,
            title,
            opened,
        })));
    }

    private string FormatSection(int section) =>
        $"{section} ({ResolveSectionTitle(section)})";

    private static string ResolveSectionTitle(int section) =>
        Application.Current?.TryFindResource($"S_Section{section:00}_Title") as string
        ?? $"S_Section{section:00}_Title";
}

internal sealed class ScuFocusUtilityTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuFocusUtilityTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "focus_scu_utility";

    public string Description =>
        "Открывает родной раздел функции SCU и подсвечивает саму утилиту. " +
        "utility_id берётся из результата search_scu_help (например, row_animations, " +
        "privacy_telemetry, clean_temp).";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object(
        ("utility_id", "string", ScuAiSchemaBuilder.UtilityIdDescription, true)));

    public ScuAiRiskLevel Risk => ScuAiRiskLevel.Navigate;

    public Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var utilityId = arguments.TryGetProperty("utility_id", out var idElement)
            ? idElement.GetString() ?? string.Empty
            : string.Empty;

        // Только зарегистрированная capability SCU — произвольный id не откроет
        // ничего (п. 38: нет фиктивного фокуса).
        var capability = _deps.Capabilities.Find(utilityId);
        if (capability is null)
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.CapabilityNotFound,
                L.T("Неизвестный id функции SCU: {0}", utilityId)));
        }

        _deps.Environment.HighlightUtility(capability.Section, capability.Title);
        return Task.FromResult(ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            utility_id = utilityId,
            section = capability.Section,
            title = capability.Title,
            focused = true,
        })));
    }
}
