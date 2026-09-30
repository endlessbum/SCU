using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SCU.Common;

namespace SCU.Models;

/// <summary>
/// Структурированный результат одной диагностической проверки:
/// проблема → причина → доказательства → решения.
/// Находки пересобираются после каждого запуска проверки (fix → verify).
/// </summary>
public sealed partial class DiagnosticFinding : ObservableObject
{
    public required string Id { get; init; }

    public required DiagnosticCategory Category { get; init; }

    public required DiagnosticSeverity Severity { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// Состояние скрыто пользователем («Пропустить»): не учитывается в сводке
    /// и не показывается среди карточек, доступно для возврата внизу раздела.
    /// </summary>
    [ObservableProperty]
    private bool _isSkipped;

    /// <summary>Почему это важно — одна строка для карточки.</summary>
    public string WhyItMatters { get; init; } = string.Empty;

    public string? ExpectedState { get; init; }

    public string? ActualState { get; init; }

    public IReadOnlyList<DiagnosticEvidence> Evidence { get; init; } = [];

    public IReadOnlyList<DiagnosticAction> Actions { get; init; } = [];

    /// <summary>Заголовок группы «Проблемы / Предупреждения / Информация» для UI.</summary>
    public string GroupTitle => L.T(Severity switch
    {
        DiagnosticSeverity.Critical => "Проблемы",
        DiagnosticSeverity.Warning => "Предупреждения",
        _ => "Информация"
    });
}

/// <summary>Сводка одного запуска диагностики для UI.</summary>
public sealed record DiagnosticRunSummary(
    int Problems,
    int Warnings,
    int Infos,
    int CompletedChecks,
    int TotalChecks)
{
    public bool IsClean => Problems == 0 && Warnings == 0;
}
