namespace SCU.Models;

/// <summary>
/// Тяжесть диагностического состояния. Info — не неисправность:
/// сознательная настройка пользователя не превращается в ошибку.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Всё в норме — правило не создало карточку.</summary>
    Healthy = 0,

    /// <summary>Наблюдаемое состояние без признаков неполадки.</summary>
    Info = 1,

    /// <summary>Требует внимания, но система работает.</summary>
    Warning = 2,

    /// <summary>Критичное состояние — мешает работе системы.</summary>
    Critical = 3
}
