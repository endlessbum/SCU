using SCU.Interop;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Быстрая read-only проверка хранилища компонентов: DISM /CheckHealth
/// (только флаг, /ScanHealth и /RestoreHealth — не здесь).
/// Стабильного API у DISM нет, поэтому текст разбирается по устойчивым маркерам
/// обеих локализаций; нечитаемый результат даёт null — правила молчат.
/// </summary>
public sealed class SystemFileProbe : DiagnosticProbe
{
    private readonly LongProcessRunner _runner;

    public SystemFileProbe(LongProcessRunner runner) => _runner = runner;

    public override string Id => "sysfiles";

    public override string Title => "Проверка системных файлов";

    public override async Task CollectAsync(DiagnosticContext context, CancellationToken ct)
    {
        var result = await _runner
            .RunAsync("DISM.exe", ["/Online", "/Cleanup-Image", "/CheckHealth"], null, ct)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            // Ошибка запуска/прав — состояние неизвестно, без находок.
            return;
        }

        var output = result.Value ?? string.Empty;
        context.ComponentStoreSample = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        var lower = output.ToLowerInvariant();
        var corruptionReported = lower.Contains("поврежден") || lower.Contains("corrupt");
        var noCorruptionReported = lower.Contains("не обнаружено")
            || lower.Contains("no component store corruption");

        context.ComponentStoreCorrupted = corruptionReported && !noCorruptionReported;
    }
}
