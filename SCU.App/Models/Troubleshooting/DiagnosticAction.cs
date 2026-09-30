namespace SCU.Models;

public enum DiagnosticActionKind
{
    /// <summary>Переход в существующий раздел SCU (не дублируем его функционал).</summary>
    OpenSection,

    /// <summary>Открыть системное приложение по URI (например, windowsdefender:).</summary>
    OpenApp,

    /// <summary>Выполнить исправление силами вкладки (после подтверждения, если требуется).</summary>
    RunFix
}

/// <summary>
/// Одно предлагаемое решение в карточке проблемы. Диагностическое правило
/// только описывает действие; исполнение — на TroubleshootingService/ViewModel.
/// </summary>
public sealed record DiagnosticAction(
    DiagnosticActionKind Kind,
    string Title,
    string? FixId = null,
    string? Parameter = null,
    int? SectionNumber = null,
    string? AppTarget = null,
    /// <summary>Какие probes перезапустить после исправления для проверки результата.</summary>
    string[]? VerifyProbeIds = null,
    bool RequiresConfirm = false,
    string? ConfirmTitle = null,
    string? ConfirmMessage = null);
