namespace SCU.Models;

/// <summary>
/// Факт-доказательство в карточке проблемы: что наблюдалось и в каком виде.
/// Стабильные идентификаторы (имена служб, коды) не локализуются.
/// </summary>
public sealed record DiagnosticEvidence(string Label, string Value);
