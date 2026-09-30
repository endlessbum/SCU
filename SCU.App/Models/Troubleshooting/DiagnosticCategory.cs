namespace SCU.Models;

/// <summary>
/// Область диагностики. Одна область = один probe; правило может опираться
/// на данные нескольких областей (контекст собирается целиком).
/// </summary>
public enum DiagnosticCategory
{
    System,
    Disk,
    WindowsUpdate,
    Network,
    Services,
    Startup,
    Security,
    Events,
    SystemFiles,
    Drivers,
    Storage,
    Audio
}
