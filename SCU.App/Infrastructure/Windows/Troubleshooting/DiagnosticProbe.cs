namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Read-only сбор данных одной области Windows в DiagnosticContext.
/// Probe ничего не изменяет и не создаёт находок — этим занимаются правила.
/// </summary>
public abstract class DiagnosticProbe
{
    /// <summary>Стабильный идентификатор (лог, verify после исправлений).</summary>
    public abstract string Id { get; }

    /// <summary>Русское название для прогресса — ключ L.T.</summary>
    public abstract string Title { get; }

    public abstract Task CollectAsync(DiagnosticContext context, CancellationToken ct);
}
