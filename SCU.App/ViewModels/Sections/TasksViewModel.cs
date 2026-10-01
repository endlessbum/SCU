using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

public partial class TasksViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly SCURunner _runner;
    private readonly TaskManager _taskManager;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;
    private bool _backupDoneThisSession;

    public TasksViewModel(Logger logger, SCURunner runner, TaskManager taskManager, IConfirmDialogService dialogs, HistoryStore history)
    {
        _logger = logger;
        _dialogs = dialogs;
        _runner = runner;
        _taskManager = taskManager;
        _history = history;
        IsAdmin = Elevation.IsAdmin();
        IsSCUAvailable = runner.IsAvailable;
        StatusText = L.T("Список задач ещё не загружен.");
    }

    public ObservableCollection<ScheduledTaskInfo> Rows { get; } = new();

    public async Task InitializeAsync()
    {
        LastBackupPath = await TaskRunner.RunBlocking(FindLatestBackupPath).ConfigureAwait(true);
    }

    [ObservableProperty]
    private bool isAdmin;

    [ObservableProperty]
    private bool isSCUAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableAllCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private string? lastBackupPath;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private ScheduledTaskInfo? selectedRow;

    public string BackupPathText => string.IsNullOrWhiteSpace(LastBackupPath)
        ? L.T("бэкап ещё не создавался")
        : LastBackupPath;

    public bool HasBackup => !string.IsNullOrWhiteSpace(LastBackupPath) && File.Exists(LastBackupPath);

    // Есть ли хотя бы одна задача, доступная для отключения (кнопка «Отключить все»).
    public bool HasEnabledRows => Rows.Any(r => r.CanDisable);

    public string ReadOnlyHint
    {
        get
        {
            if (!IsSCUAvailable)
            {
                return L.T("SCU.ps1 недоступен, изменение задач невозможно.");
            }

            if (!IsAdmin)
            {
                return L.T("Нужны права администратора. Список доступен только для чтения.");
            }

            return string.Empty;
        }
    }

    partial void OnLastBackupPathChanged(string? value)
    {
        OnPropertyChanged(nameof(BackupPathText));
        OnPropertyChanged(nameof(HasBackup));
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление списка задач", async ct =>
        {
            StatusText = L.T("Чтение задач планировщика…");
            var loaded = await LoadRowsAsync(ct).ConfigureAwait(true);
            if (!loaded)
            {
                return;
            }

            // П.19: без фразы о правах администратора; путь бэкапа — с новой строки.
            StatusText = L.T("Загружено задач: {0}.", Rows.Count) + "\n" + L.T("Бэкап: {0}", BackupPathText);
            _logger.Info($"TASKS | refresh | count={Rows.Count} | admin={IsAdmin}");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private async Task BackupAsync()
    {
        await RunExclusiveAsync("бэкап задач", ct => BackupCoreAsync(ct)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (!HasBackup || string.IsNullOrWhiteSpace(LastBackupPath))
        {
            return;
        }

        await RunExclusiveAsync("восстановление задач", async ct =>
        {
            StatusText = L.T("Восстановление задач из бэкапа…");
            _logger.Info("TASKS | restore | file=" + LastBackupPath);
            var result = await _runner.RunAsync(
                "TasksRestore",
                new Dictionary<string, string?>
                {
                    ["InFile"] = LastBackupPath
                },
                progress: null,
                ct).ConfigureAwait(true);

            if (result.IsSuccess)
            {
                StatusText = L.T("Откат выполнен. {0}", result.Message);
                _logger.Info("TASKS | restore ok");
            }
            else
            {
                StatusText = L.T("Откат завершился с кодом {0}: {1}", result.Code, result.Message);
                _logger.Error($"TASKS | restore rc={result.Code} | {result.Message}");
            }

            await LoadRowsAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private async Task DisableAsync(ScheduledTaskInfo? row)
    {
        if (row is null || !row.CanDisable)
        {
            return;
        }

        var confirmed = _dialogs.Ask(
            L.T("Отключение задачи"),
            L.T("Отключить задачу «{0}»?\nПуть: {1}", row.Name, row.FullPath),
            L.T("Отключить"));
        if (!confirmed)
        {
            return;
        }

        await RunExclusiveAsync("отключение " + row.Name, async ct =>
        {
            if (!_backupDoneThisSession)
            {
                var backup = await BackupCoreAsync(ct).ConfigureAwait(true);
                if (!backup)
                {
                    return;
                }
            }

            StatusText = L.T("Отключение задачи {0}…", row.Name);
            _logger.Info("TASKS | disable | " + row.FullPath);

            var result = await _runner.RunAsync(
                "TasksDisable",
                new Dictionary<string, string?>
                {
                    ["Tasks"] = row.FullPath
                },
                progress: null,
                ct).ConfigureAwait(true);

            if (result.IsSuccess)
            {
                StatusText = L.T("Задача отключена. {0}", result.Message);
                _logger.Info("TASKS | disable ok");
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Задачи"),
                    L.T("Отключение задачи: {0}", row.Name),
                    HistoryEvent.StatusOk,
                    row.FullPath));
            }
            else
            {
                StatusText = L.T("Не удалось отключить {0} (код {1}): {2}", row.Name, result.Code, result.Message);
                _logger.Error($"TASKS | disable rc={result.Code} | {result.Message}");
            }

            await LoadRowsAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanDisableAll))]
    private async Task DisableAllAsync()
    {
        // Снимок включённых задач: BackupCoreAsync ниже перечитывает список Rows.
        var targets = Rows.Where(r => r.CanDisable).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var confirmed = _dialogs.Ask(
            L.T("Отключение задач"),
            L.T("Отключить все перечисленные задачи ({0})?\nВернуть прежние состояния можно через «Откатить».", targets.Count),
            L.T("Отключить"));
        if (!confirmed)
        {
            return;
        }

        await RunExclusiveAsync("отключение всех задач", async ct =>
        {
            if (!_backupDoneThisSession)
            {
                var backup = await BackupCoreAsync(ct).ConfigureAwait(true);
                if (!backup)
                {
                    return;
                }
            }

            StatusText = L.T("Отключение всех задач…");
            _logger.Info("TASKS | disable-all | count=" + targets.Count);

            var result = await _runner.RunAsync(
                "TasksDisable",
                new Dictionary<string, string?>
                {
                    ["Tasks"] = string.Join(";", targets.Select(t => t.FullPath))
                },
                progress: null,
                ct).ConfigureAwait(true);

            if (result.IsSuccess)
            {
                StatusText = L.T("Задачи отключены. {0}", result.Message);
                _logger.Info("TASKS | disable-all ok");
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Задачи"),
                    L.T("Отключение всех задач ({0})", targets.Count),
                    HistoryEvent.StatusOk,
                    string.Join("; ", targets.Select(t => t.Name))));
            }
            else
            {
                StatusText = L.T("Не удалось отключить задачи (код {0}): {1}", result.Code, result.Message);
                _logger.Error($"TASKS | disable-all rc={result.Code} | {result.Message}");
            }

            await LoadRowsAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | tasks operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        _operationCts?.Cancel();
    }

    private bool CanRefresh() => !IsBusy && IsSCUAvailable;

    private bool CanBackup() => !IsBusy && IsAdmin && IsSCUAvailable;

    private bool CanRestore() => !IsBusy && IsAdmin && IsSCUAvailable && HasBackup;

    private bool CanDisable(ScheduledTaskInfo? row) =>
        !IsBusy && IsAdmin && IsSCUAvailable && row is not null && row.CanDisable;

    private bool CanDisableAll() => !IsBusy && IsAdmin && IsSCUAvailable && HasEnabledRows;

    private bool CanCancel() => IsBusy;

    private async Task<bool> BackupCoreAsync(CancellationToken ct)
    {
        var directory = GetBackupDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"tasks_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

        StatusText = L.T("Сохранение бэкапа задач…");
        _logger.Info("TASKS | backup | file=" + path);

        var result = await _taskManager.ListAsync(path, ct).ConfigureAwait(true);
        if (!result.IsSuccess || result.Value is null)
        {
            StatusText = L.T("Бэкап не сохранён (код {0}): {1}", result.Code, result.Message);
            _logger.Error($"TASKS | backup rc={result.Code} | {result.Message}");
            return false;
        }

        LastBackupPath = path;
        _backupDoneThisSession = true;
        ApplyRows(result.Value);
        StatusText = L.T("Бэкап сохранён: {0}", path);
        _logger.Info("TASKS | backup ok | file=" + path);
        // П.2: хранятся только MaxFilesPerDirectory свежих бэкапов, старые удаляются.
        BackupRetention.Enforce(directory, _logger);
        return true;
    }

    private async Task<bool> LoadRowsAsync(CancellationToken ct)
    {
        var path = GetListFilePath();
        var result = await _taskManager.ListAsync(path, ct).ConfigureAwait(true);
        if (!result.IsSuccess || result.Value is null)
        {
            StatusText = L.T("Не удалось прочитать задачи: {0}", result.Message);
            _logger.Error("TASKS | list failed | " + result.Message);
            return false;
        }

        ApplyRows(result.Value);
        return true;
    }

    private void ApplyRows(IReadOnlyList<ScheduledTaskInfo> tasks)
    {
        Rows.Clear();
        foreach (var info in tasks)
        {
            Rows.Add(info);
        }

        DisableCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasEnabledRows));
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
            _logger.Error("TASKS | " + title + " | " + exception);
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
            "tasks");

    private static string GetCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU",
            "cache");

    private static string GetListFilePath() => Path.Combine(GetCacheDirectory(), "tasks_list.txt");

    private static string? FindLatestBackupPath()
    {
        var directory = GetBackupDirectory();
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(directory, "tasks_*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
