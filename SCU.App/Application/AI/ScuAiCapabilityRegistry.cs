using System.Windows;
using SCU.Models.AI;
using SCU.ViewModels.Sections;

namespace SCU.AppCore.AI;

// Реестр AI-доступных capabilities — адаптер над существующим реестром
// Dashboard (п. 5 ТЗ): единственная истина о функциях SCU — BatchUtility.
// AI capability → существующая операция SCU, новая реализация не дублируется.
//
// Capability описывает: что можно прочитать (ReadState) и как применить
// (Apply = utility.Run). Риск назначается здесь, кодом, а не моделью (п. 7 ТЗ).
public sealed class ScuAiCapabilityRegistry
{
    private readonly Logger _logger;
    private readonly IReadOnlyList<BatchUtility> _utilities;
    private readonly Dictionary<string, ScuAiCapability> _capabilities;
    private readonly UIViewModel _ui;
    private readonly InputViewModel _input;
    private readonly PrivacyViewModel _privacy;

    public ScuAiCapabilityRegistry(
        Logger logger,
        DashboardViewModel dashboard,
        UIViewModel ui,
        InputViewModel input,
        PrivacyViewModel privacy)
    {
        _logger = logger;
        _utilities = dashboard.Utilities;
        _ui = ui;
        _input = input;
        _privacy = privacy;
        _capabilities = BuildCapabilities();
    }

    // Только тесты: реестр без связи с реестром Dashboard.
    internal ScuAiCapabilityRegistry(Logger logger)
    {
        _logger = logger;
        _utilities = [];
        _ui = null!;
        _input = null!;
        _privacy = null!;
        _capabilities = [];
    }

    public ScuAiCapability? Find(string utilityId) =>
        _capabilities.TryGetValue(utilityId, out var capability) ? capability : null;

    public IReadOnlyList<ScuAiCapability> All => _capabilities.Values.ToList();

    // Только тесты: возможность, не связанная с реестром Dashboard.
    internal void InjectForTest(ScuAiCapability capability) => _capabilities[capability.UtilityId] = capability;

    // ===================== Сборка из реестра утилит =====================

    private Dictionary<string, ScuAiCapability> BuildCapabilities()
    {
        var capabilities = new Dictionary<string, ScuAiCapability>(StringComparer.Ordinal);
        foreach (var utility in _utilities)
        {
            capabilities[utility.Id] = new ScuAiCapability
            {
                UtilityId = utility.Id,
                Title = utility.ResolveTitle(),
                Section = utility.Section,
                Risk = ClassifyRisk(utility),
                HelpId = "utility_" + utility.Id,
                RequiresRestart = RequiresRestart(utility),
                RequiresElevation = !Elevation.IsAdmin(),
                // Целевое состояние: «Вкл.»/«Выкл.» у тумблеров (SCU применяет
                // тумблер в одну сторону), null у операций-кнопок.
                DesiredState = ResolveTargetText(utility),
                DesiredStateIsOn = string.Equals(utility.TargetText, "S_BatchTargetOn", StringComparison.Ordinal),
                IsOperation = utility.TargetText is null,
                // Чтение состояния — через те же VM, которыми пользуется UI:
                // второго способа чтения не делаем (п. 8 ТЗ).
                ReadState = () => ReadUtilityState(utility),
                // Применение — существующая команда/тумблер раздела.
                Apply = async () =>
                {
                    _logger.Info($"SCU_AI | action_apply | utility={utility.Id}");
                    var status = await utility.Run().ConfigureAwait(true);
                    return status is null
                        ? (false, L.T("Операция сейчас недоступна — раздел занят или нет прав администратора."))
                        : (true, status);
                },
            };
        }

        return capabilities;
    }

    // Целевое состояние из ключа локализации («S_BatchTargetOn» → «Вкл.»).
    private static string? ResolveTargetText(BatchUtility utility) =>
        utility.TargetText is { } key
            ? Application.Current?.TryFindResource(key) as string ?? key
            : null;

    // Риск по роду операции: тумблеры настроек — Mutate, потенциально опасные
    // операции (очистка панели задач, блокировка обновлений, сброс сети,
    // необратимые операции WinSXS) — HighRisk. Read-only tools здесь нет:
    // это capabilities только для prepare/apply.
    private static ScuAiRiskLevel ClassifyRisk(BatchUtility utility) => utility.Id switch
    {
        "ui_clear_taskbar" => ScuAiRiskLevel.HighRisk,
        "ui_restart_explorer" => ScuAiRiskLevel.HighRisk,
        "update_block" => ScuAiRiskLevel.HighRisk,
        "net_reset_all" => ScuAiRiskLevel.HighRisk,
        "maint_winsxs_resetbase" => ScuAiRiskLevel.HighRisk,
        "power_cpu_clear" => ScuAiRiskLevel.HighRisk,
        _ => ScuAiRiskLevel.Mutate,
    };

    // Перезагрузка нужна параметрам питания/BCD — пользователь должен знать
    // об этом до подтверждения (preview).
    private static bool RequiresRestart(BatchUtility utility) => utility.Id switch
    {
        "power_cpu_clear" or "power_pagefile_system" or "power_pagefile_6144"
            or "power_pagefile_8192" or "power_pagefile_4096" or "power_pagefile_2048"
            => true,
        _ => false,
    };

    // Текущее состояние одной утилиты — текстом для preview:
    // тумблеры — «Вкл./Выкл.», остальные — признак «применено/можно применить».
    private string ReadUtilityState(BatchUtility utility)
    {
        // Тумблеры разделов UI/Input: state — у самих строк раздела.
        if (utility.Id.StartsWith("row_", StringComparison.Ordinal))
        {
            var row = FindSwitchRow(utility.Id["row_".Length..]);
            if (row is { } switchRow)
            {
                return L.T(switchRow.IsOn ? "Вкл." : "Выкл.");
            }
        }

        // Категории приватности: IsEnabled = защита категории включена.
        if (utility.Id.StartsWith("privacy_", StringComparison.Ordinal))
        {
            var categoryId = utility.Id["privacy_".Length..];
            var privacyRow = _privacy.Rows.FirstOrDefault(candidate => candidate.Category.Id == categoryId);
            if (privacyRow is { } row)
            {
                return row.IsEnabled
                    ? L.T("Защита включена")
                    : L.T("Защита отключена");
            }
        }

        // Остальные: NeedsApply показывает, применено ли целевое состояние.
        var needsApply = utility.NeedsApply?.Invoke();
        return needsApply switch
        {
            true => L.T("Можно применить"),
            false => L.T("Применено"),
            _ => L.T("Операция"),
        };
    }

    private SwitchRow? FindSwitchRow(string switchId)
    {
        return _ui.ExplorerRows.Concat(_ui.VisualFxRows).Append(_ui.RecommendedRow)
            .Concat(_input.Rows)
            .FirstOrDefault(row => string.Equals(row.Id, switchId, StringComparison.Ordinal));
    }
}

// Одна AI-доступная capability: связь с реальной функцией SCU.
public sealed class ScuAiCapability
{
    public required string UtilityId { get; init; }

    public required string Title { get; init; }

    public int Section { get; init; }

    public ScuAiRiskLevel Risk { get; init; }

    public string HelpId { get; init; } = string.Empty;

    public bool RequiresRestart { get; init; }

    public bool RequiresElevation { get; init; }

    // Целевое состояние тумблера на языке UI («Вкл.»/«Выкл.»); null — операция-кнопка.
    public string? DesiredState { get; init; }

    // Целевое состояние — «включить» (S_BatchTargetOn) или «выключить».
    public bool DesiredStateIsOn { get; init; }

    // true — операция-кнопка (очистка, проверка, бэкап), false — тумблер.
    public bool IsOperation { get; init; }

    // Текущее состояние — человекочитаемый текст для preview.
    public Func<string> ReadState { get; set; } = () => string.Empty;

    // Применение через существующий Run утилиты: (успех, статус раздела).
    public Func<Task<(bool Success, string Message)>> Apply { get; set; } =
        () => Task.FromResult((false, "not implemented"));
}
