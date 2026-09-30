using System.Text.Json;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// Централизованный registry tools (п. 6 ТЗ):
// - содержит только явно зарегистрированные typed tools (allowlist);
// - выдаёт схемы для API;
// - находит tool по имени и валидирует сам факт его наличия;
// - разбирает и повторно валидирует JSON аргументов;
// - вызывает tool и возвращает нормализованный результат;
// - логирует lifecycle без секретов (п. 37 ТЗ).
//
// Никакого execute_command/PowerShell/registry tool здесь нет и быть не может
// (п. 38 ТЗ): то, чего нет в allowlist, выполнить невозможно.
public sealed class ScuAiToolRegistry
{
    private readonly Logger _logger;
    private readonly Dictionary<string, IScuAiTool> _tools;

    public ScuAiToolRegistry(Logger logger, IEnumerable<IScuAiTool> tools)
    {
        _logger = logger;
        _tools = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        _logger.Info($"SCU_AI | registry | tools={_tools.Count}");
    }

    public IReadOnlyCollection<IScuAiTool> All => _tools.Values;

    // Схемы для отправки в API: только то, что поддерживает провайдер
    // (модель без tool calling не получит schema мутаций — п. 26 ТЗ).
    public IReadOnlyList<ScuAiToolSchema> GetSchemas(bool supportsToolCalling)
    {
        return _tools.Values
            .Where(tool => supportsToolCalling || tool.Risk == ScuAiRiskLevel.ReadOnly || tool.Risk == ScuAiRiskLevel.Navigate)
            .Select(tool => tool.Schema)
            .ToList();
    }

    public IScuAiTool? Find(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : null;

    // Вызов tool из ответа модели: неизвестное имя, мусорный JSON, отмена и
    // внутренние ошибки — структурированный отказ, никогда fake success.
    public async Task<ScuAiToolResult> ExecuteAsync(string name, string? argumentsJson, ScuAiExecutionContext context)
    {
        var tool = Find(name);
        if (tool is null)
        {
            _logger.Warn($"SCU_AI | tool_call | rejected | unknown tool {name}");
            return ScuAiToolResult.Failure(ScuAiErrorCode.UnknownTool, L.T("Неизвестный tool: {0}", name));
        }

        // Провайдер/модель без tool calling не может выполнять мутации (п. 26 ТЗ).
        if (!context.Context.SupportsToolCalling && tool.RequiresConfirmation)
        {
            _logger.Warn($"SCU_AI | tool_call | rejected | mutating tool {name} without tool-calling support");
            return ScuAiToolResult.Failure(
                ScuAiErrorCode.ProviderUnsupported,
                L.T("Эта модель не поддерживает вызов функций — изменение настроек недоступно."));
        }

        JsonElement arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? default
                : JsonDocument.Parse(argumentsJson).RootElement;
        }
        catch (JsonException exception)
        {
            _logger.Warn($"SCU_AI | tool_call | rejected | malformed json in {name}");
            return ScuAiToolResult.Failure(
                ScuAiErrorCode.InvalidArguments,
                L.T("Невалидные аргументы tool: {0}", exception.Message));
        }

        try
        {
            _logger.Info($"SCU_AI | tool_call | name={name}");
            var started = Stopwatch.GetTimestamp();
            var result = await tool.ExecuteAsync(arguments, context).ConfigureAwait(true);
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logger.Info($"SCU_AI | tool_result | name={name} | success={result.Success} | confirm={result.RequiresConfirmation} | {duration:F0}ms");
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"SCU_AI | tool_cancelled | name={name}");
            return ScuAiToolResult.Failure(ScuAiErrorCode.Cancelled, L.T("Операция отменена."));
        }
        catch (Exception exception)
        {
            // Стек-трейс модели не отдаём, но в лог пишем для диагностики.
            _logger.Error($"SCU_AI | tool_exception | name={name} | {exception.Message}");
            return ScuAiToolResult.Failure(
                ScuAiErrorCode.ExecutionFailed,
                L.T("Внутренняя ошибка выполнения tool."));
        }
    }
}
