using System.Text.Json;
using SCU.AppCore.Help;
using SCU.Common;
using SCU.Models.AI;
using SCU.ViewModels.Sections;

namespace SCU.AppCore.AI;

// Общие зависимости всех tools: один экземпляр на приложение (п. 29 ТЗ —
// второй экземпляр каждого сервиса не создаётся). Tools получают всё, что им
// нужно, через эту обёртку: создавать сервисы внутри tools нельзя.
public sealed class ScuAiToolDeps
{
    public required Logger Logger { get; init; }

    public required IScuHelpService Help { get; init; }

    public required ScuAiActionPlanStore Plans { get; init; }

    public required ScuAiCapabilityRegistry Capabilities { get; init; }

    public required IScuAiEnvironment Environment { get; init; }

    // Существующая инфраструктура подтверждений SCU: HighRisk-операции идут
    // через тот же DestructiveChange-флоу, что и опасные операции разделов
    // (п. 3 ARCH + п. 42.11 ТЗ). null — только в тестах без UI.
    public IConfirmDialogService? Dialogs { get; init; }

    // Разделы, читаемые state tools: те же экземпляры VM, что и UI (п. 8/21 ТЗ —
    // второго способа чтения состояния не делаем, CanExecute учитывается тоже).
    public required ScuAiStateSources State { get; init; }
}

// Источники состояния для read-only tools. Никаких новых чтений реестра/WMI:
// каждый брал те же VM, которыми пользуется пользователь.
public sealed class ScuAiStateSources
{
    public required InfoViewModel Info { get; init; }

    public required PowerViewModel Power { get; init; }

    public required NetworkViewModel Network { get; init; }

    public required PrivacyViewModel Privacy { get; init; }

    public required UIViewModel Ui { get; init; }

    public required InputViewModel Input { get; init; }

    public required ServicesViewModel Services { get; init; }

    public required StartupViewModel Startup { get; init; }

    public required UpdateViewModel Update { get; init; }

    public required MaintenanceViewModel Maintenance { get; init; }

    public required TroubleshootingViewModel Troubleshooting { get; init; }
}

// Окружение приложения для контекста и навигации: реализует MainViewModel,
// чтобы AI-слой не зависел от UI и не дублировал navigation host (п. 30 ТЗ).
public interface IScuAiEnvironment
{
    int? CurrentSectionNumber { get; }

    string CurrentSectionTitle { get; }

    string? CurrentUtilityId { get; }

    string? CurrentUtilityTitle { get; }

    bool IsAdmin { get; }

    // Существующий механизм навигации SCU (MainViewModel.SelectSectionByNumber).
    bool NavigateToSection(int sectionNumber);

    // Подсветка карточки утилиты в открытом разделе (SectionHighlight).
    void HighlightUtility(int sectionNumber, string? utilityTitle);

    // Номера разделов, реально существующих в меню SCU (для валидации навигации).
    IReadOnlyList<int> AvailableSections { get; }
}

// Контекст одного вызова tool: снимок состояния приложения на момент вызова,
// токен отмены всей AI-операции и UI-события, которые tools прикрепляют к
// сообщению ассистента во время выполнения (карточки плана, результат, ошибки).
public sealed class ScuAiExecutionContext
{
    public required ScuAiContext Context { get; init; }

    public CancellationToken CancellationToken { get; init; }

    // Ссылки на внутреннюю справку, собранные в ходе выполнения (п. 17 ТЗ):
    // help tools прикрепляют их сюда, UI показывает валитированные карточки.
    public List<ScuAiHelpReference> HelpReferences { get; } = [];

    // Подготовленный план: UI показывает карточку подтверждения (п. 19 ТЗ).
    public List<ScuAiActionPlanSnapshot> Plans { get; } = [];

    // Результат применения: карточка «было → стало».
    public List<ScuAiActionResultSnapshot> Results { get; } = [];

    // Структурированные ошибки tools: модель не маскирует их под успех.
    public List<ScuAiToolFailure> Failures { get; } = [];

    public void AttachHelp(IEnumerable<ScuAiHelpReference> references) => HelpReferences.AddRange(references);

    public void AttachPlan(ScuAiActionPlanSnapshot plan) => Plans.Add(plan);

    public void AttachResult(ScuAiActionResultSnapshot result) => Results.Add(result);

    public void AttachFailure(string toolName, string message) =>
        Failures.Add(new ScuAiToolFailure(toolName, message));
}

// Ошибка tool для карточки в чате: имя tool и локализованное сообщение.
public sealed record ScuAiToolFailure(string ToolName, string Message);

// Контракт одного зарегистрированного tool (п. 6 ТЗ). Имя, описание и схема
// отдаются в API; аргументы повторно валидируются внутри ExecuteAsync —
// совпадения с JSON недостаточно, чтобы считать вызов допустимым.
public interface IScuAiTool
{
    string Name { get; }

    string Description { get; }

    ScuAiToolSchema Schema { get; }

    ScuAiRiskLevel Risk { get; }

    // Mutate/HighRisk всегда идут через preview + подтверждение (п. 7 ТЗ).
    bool RequiresConfirmation => Risk is ScuAiRiskLevel.Mutate or ScuAiRiskLevel.HighRisk;

    Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context);
}
