using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.ViewModels.Sections;

public partial class ServicesViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly SCURunner _runner;
    private readonly ServiceManager _serviceManager;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;
    private bool _backupDoneThisSession;

    public ServicesViewModel(
        Logger logger,
        SCURunner runner,
        ServiceManager serviceManager,
        HistoryStore history,
        IConfirmDialogService dialogs)
    {
        _logger = logger;
        _runner = runner;
        _serviceManager = serviceManager;
        _history = history;
        _dialogs = dialogs;
        IsAdmin = Elevation.IsAdmin();
        IsSCUAvailable = runner.IsAvailable;
        StatusText = L.T("Список служб ещё не загружен.");
    }

    public ObservableCollection<ServiceRowViewModel> Rows { get; } = new();

    public async Task InitializeAsync()
    {
        LastBackupPath = await TaskRunner.RunBlocking(FindLatestBackupPath).ConfigureAwait(true);
    }

    [ObservableProperty]
    private bool isAdmin;

    [ObservableProperty]
    private bool isSCUAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private string? lastBackupPath;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private ServiceRowViewModel? selectedRow;

    public string BackupPathText => string.IsNullOrWhiteSpace(LastBackupPath)
        ? "резерв ещё не создавался"
        : LastBackupPath;

    public bool HasBackup => !string.IsNullOrWhiteSpace(LastBackupPath) && File.Exists(LastBackupPath);

    partial void OnLastBackupPathChanged(string? value)
    {
        OnPropertyChanged(nameof(BackupPathText));
        OnPropertyChanged(nameof(HasBackup));
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление списка служб", async ct =>
        {
            StatusText = L.T("Чтение статусов служб…");
            var result = await _serviceManager.GetDefaultServicesAsync(ct).ConfigureAwait(true);
            if (!result.IsSuccess || result.Value is null)
            {
                StatusText = L.T("Не удалось прочитать службы: {0}", result.Message);
                _logger.Error("SERVICES | refresh failed | " + result.Message);
                return;
            }

            ApplyRows(result.Value);
            var rights = IsAdmin ? L.T("Права администратора есть") : L.T("Без прав администратора — изменение недоступно");
            StatusText = L.T("Загружено служб: {0}. {1}. Резерв: {2}.", Rows.Count, rights, BackupPathText);
            _logger.Info($"SERVICES | refresh | count={Rows.Count} | admin={IsAdmin}");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private async Task BackupAsync()
    {
        await RunExclusiveAsync("резерв служб", ct => BackupCoreAsync(ct)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        await RunExclusiveAsync("восстановление служб", async ct =>
        {
            if (!HasBackup || string.IsNullOrWhiteSpace(LastBackupPath))
            {
                StatusText = L.T("Нет файла резерва для отката.");
                return;
            }

            StatusText = L.T("Восстановление служб из резерва…");
            _logger.Info("SERVICES | restore | file=" + LastBackupPath);
            var result = await _runner.RunAsync(
                "ServicesRestore",
                new Dictionary<string, string?>
                {
                    ["InFile"] = LastBackupPath,
                    ["Services"] = ServiceManager.DefaultServicesCsv
                },
                progress: null,
                ct).ConfigureAwait(true);

            if (result.IsSuccess)
            {
                StatusText = L.T("Откат выполнен. {0}", result.Message);
                _logger.Info("SERVICES | restore ok");
            }
            else
            {
                StatusText = L.T("Откат завершился с кодом {0}: {1}", result.Code, result.Message);
                _logger.Error($"SERVICES | restore rc={result.Code} | {result.Message}");
            }

            await ReloadRowsAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync(ServiceRowViewModel? row)
    {
        if (row is null || !row.CanToggle)
        {
            return;
        }

        await RunExclusiveAsync("переключение службы " + row.Name, async ct =>
        {
            if (!_backupDoneThisSession)
            {
                var backup = await BackupCoreAsync(ct).ConfigureAwait(true);
                if (!backup)
                {
                    return;
                }
            }

            var disable = row.ShouldDisable;
            StatusText = L.T(disable ? "Отключение службы {0}…" : "Включение службы {0}…", row.Name);
            _logger.Info($"SERVICES | toggle | name={row.Name} | disable={disable}");

            Result result;
            if (disable)
            {
                result = await _serviceManager.SetDisabledAsync(row.Name, true, ct).ConfigureAwait(true);
            }
            else
            {
                // Включение — напрямую через .NET. ServicesRestore здесь не годится:
                // он возвращает исходное состояние из резерва, а исходно служба была выключена.
                var backupPath = HasBackup ? LastBackupPath : null;
                result = await _serviceManager.SetEnabledAsync(row.Name, backupPath, ct).ConfigureAwait(true);
            }

            if (result.IsSuccess)
            {
                StatusText = L.S(result.Message);
                _logger.Info("SERVICES | toggle ok | " + result.Message);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Службы"),
                    L.T("Служба: {0}", row.Name),
                    HistoryEvent.StatusOk,
                    row.ShouldDisable ? L.T("отключена") : L.T("включена")));
            }
            else
            {
                StatusText = L.T("Не удалось изменить {0}: {1}", row.Name, result.Message);
                _logger.Error($"SERVICES | toggle rc={result.Code} | {result.Message}");
            }

            await ReloadRowAsync(row, ct).ConfigureAwait(true);
            row.ForceStateNotify();
        }).ConfigureAwait(true);
    }

    // «Отключить все»: активна, пока хотя бы одна служба списка работает
    // (разрешён запуск); когда все отключены — неактивна.
    private bool CanDisableAll() =>
        !IsBusy && IsAdmin && IsSCUAvailable && Rows.Any(row => row.IsServiceEnabled);

    [RelayCommand(CanExecute = nameof(CanDisableAll))]
    private async Task DisableAllAsync()
    {
        var targets = Rows.Where(row => row.ShouldDisable).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        if (!_dialogs.ConfirmChange(new DestructiveChange(
                L.T("Отключение всех служб"),
                CurrentState: L.T("{0} служб(ы) в списке работают штатно:", targets.Count)
                    + "\n" + string.Join(", ", targets.Select(row => row.Name)),
                NewState: L.T("Все перечисленные службы — состояние «Отключена»."),
                Consequences: L.T("Отключённые службы перестают запускаться системой; зависящие от них программы могут потерять функциональность."),
                Rollback: L.T("Резерв текущих состояний создаётся автоматически перед изменением; кнопка «Откатить» возвращает всё как было."),
                ConfirmText: L.T("Отключить все"))))
        {
            return;
        }

        await RunExclusiveAsync("отключение всех служб", async ct =>
        {
            // Резерв — один раз перед массовыми изменениями (как перед одиночным тумблером).
            if (!_backupDoneThisSession)
            {
                var backup = await BackupCoreAsync(ct).ConfigureAwait(true);
                if (!backup)
                {
                    return;
                }
            }

            var failures = new List<string>();
            foreach (var row in targets)
            {
                ct.ThrowIfCancellationRequested();
                StatusText = L.T("Отключение службы {0}…", row.Name);
                _logger.Info("SERVICES | disable all | " + row.Name);
                var result = await _serviceManager.SetDisabledAsync(row.Name, true, ct).ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    // Отсутствующая служба возвращается с «пропущена» — это не сбой массовой операции.
                    _history.Enqueue(new HistoryEvent(
                        DateTime.Now,
                        L.T("Службы"),
                        L.T("Служба: {0}", row.Name),
                        HistoryEvent.StatusOk,
                        L.T("отключена")));
                }
                else
                {
                    failures.Add(L.T("{0}: {1}", row.Name, L.S(result.Message)));
                    _logger.Error($"SERVICES | disable all rc={result.Code} | {row.Name} | {result.Message}");
                }
            }

            StatusText = failures.Count == 0
                ? L.T("Все службы отключены.")
                : L.T("Отключение служб: часть операций не удалась — {0}", string.Join("; ", failures.Select(L.S)));

            // Фактические состояния после операции — источник правды для тумблеров.
            await ReloadRowsAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | services operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        _operationCts?.Cancel();
    }

    private bool CanRefresh() => !IsBusy;

    private bool CanBackup() => !IsBusy && IsAdmin && IsSCUAvailable;

    private bool CanRestore() => !IsBusy && IsAdmin && IsSCUAvailable && HasBackup;

    private bool CanToggle(ServiceRowViewModel? row) =>
        !IsBusy && IsAdmin && IsSCUAvailable && row is not null && row.CanToggle;

    private bool CanCancel() => IsBusy;

    private async Task<bool> BackupCoreAsync(CancellationToken ct)
    {
        if (!IsSCUAvailable)
        {
            StatusText = L.T("SCU.ps1 недоступен, резерв не создан.");
            return false;
        }

        var directory = GetBackupDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"services_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

        StatusText = L.T("Сохранение резерва служб…");
        _logger.Info("SERVICES | backup | file=" + path);

        var result = await _runner.RunAsync(
            "ServicesBackup",
            new Dictionary<string, string?>
            {
                ["OutFile"] = path,
                ["Services"] = ServiceManager.DefaultServicesCsv
            },
            progress: null,
            ct).ConfigureAwait(true);

        if (result.IsSuccess)
        {
            LastBackupPath = path;
            _backupDoneThisSession = true;
            StatusText = L.T("Резерв сохранён: {0}", path);
            _logger.Info("SERVICES | backup ok | file=" + path);
            // П.2: хранятся только MaxFilesPerDirectory свежих резервов, старые удаляются.
            BackupRetention.Enforce(directory, _logger);
            return true;
        }

        StatusText = L.T("Резерв не сохранён (код {0}): {1}", result.Code, result.Message);
        _logger.Error($"SERVICES | backup rc={result.Code} | {result.Message}");
        return false;
    }

    private async Task ReloadRowsAsync(CancellationToken ct)
    {
        var result = await _serviceManager.GetDefaultServicesAsync(ct).ConfigureAwait(true);
        if (!result.IsSuccess || result.Value is null)
        {
            StatusText = L.T("{0} Список не обновлён: {1}", StatusText, result.Message);
            return;
        }

        ApplyRows(result.Value);
        StatusText = L.T("{0} Список обновлён ({1}).", StatusText, Rows.Count);
    }

    private async Task ReloadRowAsync(ServiceRowViewModel row, CancellationToken ct)
    {
        var result = await _serviceManager.GetServiceAsync(row.Name, ct).ConfigureAwait(true);
        if (!result.IsSuccess || result.Value is null)
        {
            await ReloadRowsAsync(ct).ConfigureAwait(true);
            return;
        }

        row.Apply(result.Value);
        ToggleCommand.NotifyCanExecuteChanged();
        DisableAllCommand.NotifyCanExecuteChanged();
    }

    private void ApplyRows(IReadOnlyList<WindowsServiceInfo> services)
    {
        Rows.Clear();
        foreach (var info in services)
        {
            Rows.Add(ServiceRowViewModel.From(info));
        }

        ToggleCommand.NotifyCanExecuteChanged();
        DisableAllCommand.NotifyCanExecuteChanged();
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
            _logger.Error("SERVICES | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string GetBackupDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU",
            "backup",
            "services");

    private static string? FindLatestBackupPath()
    {
        var directory = GetBackupDirectory();
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(directory, "services_*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
