using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Infrastructure.Windows.Tweaks;
using SCU.Interop;

namespace SCU.ViewModels.Sections;

// Раздел 27 «Windows Defender» — подвкладка группы «Приватность и защита»:
// карточка «Защитник Windows» со скриптовым выключением/включением Защитника
// на уровне системы (SCU.ps1: defenderdisable / defenderenable). Модель —
// PrivacyViewModel (RunExclusiveAsync, подтверждение DestructiveChange,
// фактическое состояние как источник правды для тумблера).
public partial class DefenderViewModel : ObservableObject, ISectionOperationCancellable, IDisposable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly SCURunner _runner;
    private readonly DefenderService _service;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = L.T("Показано текущее состояние Защитника Windows.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isAdmin;

    // Для тумблера карточки: true = Защитник работает, false = выключен.
    [ObservableProperty]
    private bool _isEnabled;

    public DefenderViewModel(Logger logger, SCURunner runner, IConfirmDialogService dialogs)
    {
        _logger = logger;
        _dialogs = dialogs;
        _runner = runner;
        IsAdmin = Elevation.IsAdmin();
        _service = new DefenderService();

        // Чтение реестра — дешёвая операция, поэтому начальное состояние тумблера
        // берётся синхронно: карточка не мигает «по умолчанию» до первой загрузки.
        IsEnabled = !DefenderService.IsDisabled();
    }

    public bool IsInteractive => !IsBusy && IsAdmin;

    public string ReadOnlyHint =>
        IsAdmin
            ? string.Empty
            : L.T("Нужны права администратора — управление Защитником Windows недоступно.");

    private bool CanModify() => !IsBusy && IsAdmin;
    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление Защитника", async ct =>
        {
            var disabled = await TaskRunner.RunBlocking(
                () => DefenderService.IsDisabled(),
                ct).ConfigureAwait(true);
            IsEnabled = !disabled;
            StatusText = L.T("Состояние Защитника Windows обновлено.");
        }).ConfigureAwait(true);
    }

    // Тумблер карточки: цель = новое состояние тумблера (связка TwoWay
    // переключает его до команды; при отказе/сбое — ресинк IsEnabled).
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleAsync()
    {
        var enable = IsEnabled;

        if (!enable && !_dialogs.ConfirmChange(new DestructiveChange(
                L.T("Выключение — Защитник Windows"),
                CurrentState: L.T("Защитник Windows работает."),
                NewState: L.T("Защитник Windows полностью выключается на уровне системы: политика DisableAntiSpyware, мониторинг в реальном времени и службы WinDefend/WdNisSvc/SecurityHealthService."),
                Consequences: L.T("Защита в реальном времени и антивирусные проверки перестают работать до повторного включения — система остаётся без антивируса."),
                Rollback: L.T("Состояния служб сохраняются в бэкап; включение восстанавливает их, а при отсутствии бэкапа — заводские значения."),
                ConfirmText: L.T("Отключить"))))
        {
            IsEnabled = !enable;
            return;
        }

        await RunExclusiveAsync(enable ? "включение Защитника Windows" : "выключение Защитника Windows", async ct =>
        {
            try
            {
                StatusText = enable
                    ? L.T("Включение Защитника Windows…")
                    : L.T("Выключение Защитника Windows…");

                var result = enable
                    ? await _service.EnableAsync(_runner, ct).ConfigureAwait(true)
                    : await _service.DisableAsync(_runner, ct).ConfigureAwait(true);

                // Фактическое состояние после операции — источник правды для тумблера.
                var actual = await TaskRunner.RunBlocking(
                    () => DefenderService.IsDisabled(),
                    ct).ConfigureAwait(true);
                IsEnabled = !actual;

                if (result.IsSuccess)
                {
                    StatusText = L.T(actual
                        ? "Защитник Windows: выключено. "
                        : "Защитник Windows: включено. ") + L.S(result.Message)
                        // Пробел — вне L.T: ведущий пробел в ключе ломал точный
                        // поиск по словарю, и фраза оставалась русской (аудит, п. 18).
                        + (enable ? " " + L.T("Если защита не вернулась, перезагрузите ПК.") : string.Empty);
                    _logger.Info($"DEFENDER | {(actual ? "disabled" : "enabled")}");
                }
                else
                {
                    StatusText = L.T("Ошибка (код {0}): {1}", result.Code, L.S(result.Message));
                    _logger.Error($"DEFENDER | {(enable ? "enable" : "disable")} | rc={result.Code} | {result.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                // Ресинк тумблера на пути отмены: чтение без токена, best-effort.
                try
                {
                    var actual = await TaskRunner.RunBlocking(
                        () => DefenderService.IsDisabled(),
                        CancellationToken.None).ConfigureAwait(true);
                    IsEnabled = !actual;
                }
                catch
                {
                    // Не маскируем исходную отмену ошибкой чтения.
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | defender operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();

    public void Dispose()
    {
        _operationCts?.Cancel();
    }

    private Task RunExclusiveAsync(string title, Func<CancellationToken, Task> action) =>
        SectionOperations.RunExclusiveAsync(title, "DEFENDER", () => IsBusy, value => IsBusy = value, () => _operationCts, value => _operationCts = value, value => StatusText = value, _logger, action);
}
