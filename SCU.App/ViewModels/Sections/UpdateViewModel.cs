using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Раздел 16 «Обновления Windows»: состояние служб обновления, пауза на N дней,
// запрет авто-драйверов, блокировка служб (ServicesBackup -> SetDisabled -> ServicesRestore).
public partial class UpdateViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly SCURunner _runner;
    private readonly UpdateService _updateService;
    private readonly ServiceManager _serviceManager;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnpauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleDriverExclusionCommand))]
    [NotifyCanExecuteChangedFor(nameof(BlockUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnblockUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isAdmin;

    public bool IsInteractive => !IsBusy && IsAdmin;

    [ObservableProperty]
    private string _statusText = L.T("Загрузка состояния…");

    [ObservableProperty]
    private string _pauseInfoText = string.Empty;

    // Блокировка обновлений (wuauserv в «Отключена») — управляет доступностью кнопок.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BlockUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnblockUpdatesCommand))]
    private bool _isWuBlocked;

    // Активная пауза обновлений — управляет доступностью «Пауза»/«Снять паузу».
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnpauseCommand))]
    private bool _isWuPaused;

    private bool _driverUpdatesExcluded;

    public bool DriverUpdatesExcluded
    {
        get => _driverUpdatesExcluded;
        set
        {
            _driverUpdatesExcluded = value;
            OnPropertyChanged(nameof(DriverUpdatesExcluded));
            OnPropertyChanged(nameof(DriverActionText));
        }
    }

    // Постоянное описание — не зависит от ON/OFF, без «Включить:/Отключить:».
    public string DriverActionText =>
        System.Windows.Application.Current?.TryFindResource("S_WuDriversDesc") as string ?? string.Empty;

    public UpdateViewModel(Logger logger, SCURunner runner, IConfirmDialogService dialogs, HistoryStore history)
    {
        _logger = logger;
        _dialogs = dialogs;
        _runner = runner;
        _history = history;
        _updateService = new UpdateService(logger, new RegistryHelper(logger));
        _serviceManager = new ServiceManager();
        IsAdmin = Elevation.IsAdmin();
        StatusText = L.T("Состояние обновлений ещё не загружено.");
        // Подпись действия тумблера драйверов перечитывается при смене языка
        // (живёт столько же, сколько приложение).
        L.LanguageChanged += RefreshDriverActionText;
    }

    private void RefreshDriverActionText() => OnPropertyChanged(nameof(DriverActionText));

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — управление обновлениями недоступно.");

    private bool CanModify() => !IsBusy && IsAdmin;
    private bool CanRefresh() => !IsBusy;
    private bool CanCancel() => IsBusy;
    private bool CanBlock() => CanModify() && !IsWuBlocked;
    private bool CanUnblock() => CanModify() && IsWuBlocked;
    private bool CanPause() => CanModify() && !IsWuPaused;
    private bool CanUnpause() => CanModify() && IsWuPaused;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление состояния", async ct =>
        {
            StatusText = L.T("Чтение состояния служб обновления…");
            await RefreshStatesAsync(ct).ConfigureAwait(true);
            StatusText = L.T("Состояние обновлено.");
        }).ConfigureAwait(true);
    }

    private async Task RefreshStatesAsync(CancellationToken ct)
    {
        // Информер-перечисление служб («wuauserv — выполняется — Manual, …, Блокировка: …»)
        // удалён из UI (п. ТЗ 20): шумная строка, состояние блокировки видно по кнопкам.
        // Читаются только флаги, управляющие доступностью операций.
        // Реестр — IO: не на UI-потоке.
        var state = await TaskRunner.RunBlocking(
            () => (
                blocked: UpdateService.IsBlocked(),
                paused: UpdateService.IsPaused(),
                pauseInfo: UpdateService.GetPauseInfoText(),
                driversExcluded: UpdateService.IsDriverUpdatesExcluded()),
            ct).ConfigureAwait(true);
        IsWuBlocked = state.blocked;
        IsWuPaused = state.paused;
        PauseInfoText = state.pauseInfo;
        DriverUpdatesExcluded = state.driversExcluded;
    }

    private static Task<(bool paused, string pauseInfo)> ReadPauseStateAsync(CancellationToken ct) =>
        TaskRunner.RunBlocking(() => (UpdateService.IsPaused(), UpdateService.GetPauseInfoText()), ct);

    private static Task<bool> ReadDriverExcludedAsync(CancellationToken ct) =>
        TaskRunner.RunBlocking(UpdateService.IsDriverUpdatesExcluded, ct);

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseUpdatesAsync()
    {
        await RunExclusiveAsync("пауза обновлений", async ct =>
        {
            StatusText = L.T("Приостановка обновлений на 7 дней…");
            var result = await TaskRunner.RunBlocking(() => _updateService.PauseUpdates(7), ct).ConfigureAwait(true);
            var after = await ReadPauseStateAsync(ct).ConfigureAwait(true);
            IsWuPaused = after.paused;
            PauseInfoText = after.pauseInfo;
            StatusText = result.IsSuccess
                ? L.T("Обновления приостановлены на 7 дней.")
                : L.T("Ошибка: {0}", result.Message);
            if (result.IsSuccess)
            {
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Обновления"),
                    L.T("Пауза обновлений (7 дней)"),
                    HistoryEvent.StatusOk));
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanUnpause))]
    private async Task UnpauseAsync()
    {
        await RunExclusiveAsync("снятие паузы обновлений", async ct =>
        {
            StatusText = L.T("Снятие паузы обновлений…");
            var result = await TaskRunner.RunBlocking(() => _updateService.UnpauseUpdates(), ct).ConfigureAwait(true);
            var after = await ReadPauseStateAsync(ct).ConfigureAwait(true);
            IsWuPaused = after.paused;
            PauseInfoText = after.pauseInfo;
            StatusText = result.IsSuccess ? L.T("Пауза снята.") : L.T("Ошибка: {0}", result.Message);
            if (result.IsSuccess)
            {
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Обновления"),
                    L.T("Снятие паузы"),
                    HistoryEvent.StatusOk));
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleDriverExclusionAsync()
    {
        var exclude = DriverUpdatesExcluded;
        await RunExclusiveAsync("драйверы через Windows Update", async ct =>
        {
            StatusText = exclude ? L.T("Разрешение установки драйверов через Windows Update…") : L.T("Запрет авто-драйверов…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _updateService.SetDriverUpdatesExcluded(exclude), ct).ConfigureAwait(true);
                DriverUpdatesExcluded = await ReadDriverExcludedAsync(ct).ConfigureAwait(true);
                StatusText = result.IsSuccess
                    ? exclude
                        ? L.T("Драйверы снова устанавливаются через Windows Update.")
                        : L.T("Авто-установка драйверов через Windows Update запрещена.")
                    : L.T("Ошибка: {0}", result.Message);
            }
            catch (OperationCanceledException)
            {
                // Ресинк тумблера на пути отмены: чтение best-effort, без токена, вне UI-потока.
                try
                {
                    DriverUpdatesExcluded = await ReadDriverExcludedAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch
                {
                    // Не маскируем исходную отмену ошибкой чтения.
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanBlock))]
    private async Task BlockUpdatesAsync()
    {
        if (!_dialogs.Ask(
                L.T("Блокировка обновлений Windows"),
                L.T("Состояние служб wuauserv, UsoSvc, WaaSMedicSvc, DoSvc, BITS будет сохранено в бэкап,\nзатем службы переведены в «Отключена».\nWindows перестанет ставить обновления до команды «Вернуть обновления».\nРекомендуется создать точку восстановления (раздел «Поиск и целостность»). Заблокировать?"),
                L.T("Заблокировать")))
        {
            return;
        }

        await RunExclusiveAsync("блокировка обновлений", async ct =>
        {
            var backupFile = GetBlockBackupPath();
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);

            if (File.Exists(backupFile))
            {
                // Повторная блокировка не перезаписывает бэкап: в нём состояние служб
                // ДО первой блокировки. Перезапись уже-отключённым состоянием сделала бы
                // «Вернуть обновления» невозвратимой.
                _logger.Info("WU | block | reuse existing backup " + backupFile);
            }
            else
            {
                StatusText = L.T("Сохранение состояния служб…");
                var backup = await _runner.RunAsync(
                    "ServicesBackup",
                    new Dictionary<string, string?> { ["OutFile"] = backupFile, ["Services"] = UpdateService.UpdateServicesCsv },
                    null,
                    ct).ConfigureAwait(true);
                if (!backup.IsSuccess || !File.Exists(backupFile))
                {
                    StatusText = L.T("Блокировка отменена: не удалось сохранить состояние служб (код {0}).", backup.Code);
                    _logger.Error($"WU | block | backup rc={backup.Code} | {backup.Message}");
                    return;
                }
            }

            var failures = new List<string>();
            try
            {
                foreach (var serviceName in UpdateService.UpdateServiceNames)
                {
                    StatusText = L.T("Отключение службы {0}…", serviceName);
                    var disable = await _serviceManager.SetDisabledAsync(serviceName, true, ct).ConfigureAwait(true);
                    _logger.Info("WU | block | " + disable.Message);
                    if (!disable.IsSuccess)
                    {
                        failures.Add($"{serviceName}: {disable.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Отмена посреди цикла могла оставить часть служб отключённой —
                // но бэкап цел, и «Вернуть обновления» восстановит исходное состояние.
                _logger.Warn("WU | block cancelled mid-way; unblock available");
                StatusText = L.T("Операция отменена. Часть служб могла остаться отключённой — используйте «Вернуть обновления».");
                return;
            }

            await RefreshStatesAsync(ct).ConfigureAwait(true);
            if (failures.Count > 0)
            {
                var message = L.T("Не все службы обновления отключены: {0}", string.Join("; ", failures));
                StatusText = L.T("Ошибка: {0}", message);
                _logger.Error("WU | block partial failure | " + message);
                return;
            }

            StatusText = L.T("Обновления заблокированы. Вернуть — кнопкой «Вернуть обновления».");
            _logger.Info("WU | block ok");
            _history.Enqueue(new HistoryEvent(
                DateTime.Now,
                L.T("Обновления"),
                L.T("Блокировка обновлений"),
                HistoryEvent.StatusOk));
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanUnblock))]
    private async Task UnblockUpdatesAsync()
    {
        var backupFile = GetBlockBackupPath();
        if (!File.Exists(backupFile))
        {
            StatusText = L.T("Бэкап служб не найден — блокировка не выполнялась из приложения.");
            return;
        }

        await RunExclusiveAsync("возврат обновлений", async ct =>
        {
            StatusText = L.T("Восстановление исходного состояния служб…");
            // Восстановление служб обязано выполниться при любом исходе: прерванная
            // отмена оставила бы службы в промежуточном состоянии (как в CleanupViewModel).
            var restore = await _runner.RunAsync(
                "ServicesRestore",
                new Dictionary<string, string?> { ["InFile"] = backupFile },
                null,
                CancellationToken.None).ConfigureAwait(true);

            await RefreshStatesAsync(ct).ConfigureAwait(true);
            StatusText = restore.IsSuccess
                ? L.T("Службы обновления возвращены к исходному состоянию.")
                : L.T("Ошибка восстановления (код {0}): {1}. Частично можно исправить через раздел «Службы Windows».", restore.Code, restore.Message);
            _logger.Info($"WU | unblock | rc={restore.Code}");

            // Успешный возврат завершает цикл: следующий «Заблокировать» снимет свежий бэкап.
            // При частичном сбое бэкап сохраняем — повторный возврат по нему ещё возможен.
            if (restore.IsSuccess)
            {
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Обновления"),
                    L.T("Возврат обновлений"),
                    HistoryEvent.StatusOk));
                try
                {
                    File.Delete(backupFile);
                }
                catch (Exception exception)
                {
                    _logger.Warn("WU | unblock | backup cleanup failed: " + exception.Message);
                }
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | update operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    private static string GetBlockBackupPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "services", "wu_block_backup.txt");

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        // Операция может быть ещё жива: только Cancel, без Dispose токен-источника —
        // Dispose при работающей операции даёт ObjectDisposedException на следующий Cancel.
        L.LanguageChanged -= RefreshDriverActionText;
        _operationCts?.Cancel();
    }

    private async Task RunExclusiveAsync(string title, Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await action(_operationCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = L.T("Операция отменена.");
            _logger.Warn("CANCEL | " + title);
        }
        catch (Exception exception)
        {
            StatusText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("WU | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
