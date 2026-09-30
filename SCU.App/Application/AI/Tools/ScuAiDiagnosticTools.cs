using System.Text.Json;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// Diagnostic tools (п. 22 ТЗ): AI — оболочка над существующей подсистемой
// troubleshooting. Запуск проб идёт через команду раздела, а не новым путём.
internal sealed class ScuRunDiagnosticsTool : IScuAiTool
{
    private readonly ScuAiToolDeps _deps;

    public ScuRunDiagnosticsTool(ScuAiToolDeps deps) => _deps = deps;

    public string Name => "run_scu_diagnostics";

    public string Description =>
        "Запускает диагностику неполадок Windows существующими пробами SCU " +
        "(может занять около минуты). Возвращает найденные проблемы и предупреждения. " +
        "Используй при жалобах на тормоза, долгую загрузку/выключение, ошибки.";

    public ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object());

    // Пробы только читают состояние системы — ничего не меняют.
    public ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public async Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var ts = _deps.State.Troubleshooting;
        if (!ts.RunCommand.CanExecute(null))
        {
            return ScuAiToolResult.Failure(
                ScuAiErrorCode.ExecutionFailed,
                L.T("Диагностика уже выполняется — дождитесь окончания."));
        }

        await ts.RunCommand.ExecuteAsync(null).ConfigureAwait(true);

        return ScuAiToolResult.Ok(JsonSerializer.Serialize(new
        {
            problems = ts.ProblemsCount,
            warnings = ts.WarningsCount,
            is_clean = ts.IsClean,
            findings = ts.Findings.Select(finding => new
            {
                title = finding.Title,
                description = finding.Description,
                severity = finding.Severity.ToString(),
                why = finding.WhyItMatters,
                expected = finding.ExpectedState,
                actual = finding.ActualState,
                actions = finding.Actions.Select(action => action.Title),
            }),
        }));
    }
}
