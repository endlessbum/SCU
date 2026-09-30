using SCU.Infrastructure.Updates;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Состояние Windows Update: пауза и блокировка через реестр (те же величины,
/// что читает раздел 16 «Обновления Windows» — без дублирования логики).
/// Состояния служб обновления собирает ServicesProbe.
/// </summary>
public sealed class UpdateProbe : DiagnosticProbe
{
    public override string Id => "update";

    public override string Title => "Проверка Windows Update";

    public override Task CollectAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        context.UpdatePaused = UpdateService.IsPaused();
        context.UpdateBlocked = UpdateService.IsBlocked();
        context.UpdatePauseInfo = UpdateService.GetPauseInfoText();
    }, CancellationToken.None);
}
