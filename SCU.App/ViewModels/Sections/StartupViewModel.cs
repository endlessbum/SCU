using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Services.Dashboard;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

public partial class StartupViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly SCURunner _runner;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;

    public StartupViewModel(Logger logger, SCURunner runner, IConfirmDialogService dialogs, HistoryStore history)
    {
        _logger = logger;
        _dialogs = dialogs;
        _runner = runner;
        _history = history;
        IsAdmin = Elevation.IsAdmin();
        IsSCUAvailable = runner.IsAvailable;
        StatusText = L.T("Список автозагрузки ещё не загружен.");
    }

    public ObservableCollection<StartupItem> Rows { get; } = new();

    public async Task InitializeAsync()
    {
        HasRestoreManifest = await TaskRunner.RunBlocking(() => File.Exists(GetManifestPath())).ConfigureAwait(true);
    }

    [ObservableProperty]
    private bool isAdmin;

    [ObservableProperty]
    private bool isSCUAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool hasRestoreManifest;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private StartupItem? selectedRow;

    public string ReadOnlyHint
    {
        get
        {
            if (!IsSCUAvailable)
            {
                return L.T("SCU.ps1 недоступен, изменение автозагрузки невозможно.");
            }

            if (!IsAdmin)
            {
                return L.T("Нужны права администратора. Список доступен только для чтения.");
            }

            return string.Empty;
        }
    }

    public string RestoreHint => HasRestoreManifest
        ? GetBackupDirectory()
        : "манифест ещё не создавался";

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление автозагрузки", async ct =>
        {
            StatusText = L.T("Чтение автозагрузки…");
            var loaded = await LoadRowsAsync(ct).ConfigureAwait(true);
            if (!loaded)
            {
                return;
            }

            var rights = IsAdmin ? "Права администратора есть" : "Без прав администратора — изменение недоступно";
            StatusText = L.T("Загружено элементов: {0}. {1}.", Rows.Count, rights);
            _logger.Info($"STARTUP | refresh | count={Rows.Count} | admin={IsAdmin}");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private async Task DisableAsync(StartupItem? row)
    {
        if (row is null || IsJunk(row))
        {
            return;
        }

        var confirmed = _dialogs.Ask(
            L.T("Отключение автозагрузки"),
            L.T("Отключить элемент «{0}» ({1})?\nКоманда: {2}", row.Name, row.Source, row.Command),
            L.T("Отключить"));
        if (!confirmed)
        {
            return;
        }

        await RunExclusiveAsync("отключение " + row.Name, async ct =>
        {
            StatusText = L.T("Отключение «{0}»…", row.Name);
            _logger.Info($"STARTUP | disable | index={row.Index} | name={row.Name}");

            var result = await _runner.RunAsync(
                "StartupDisable",
                new Dictionary<string, string?>
                {
                    ["ListFile"] = GetListFilePath(),
                    ["Index"] = row.Index.ToString(CultureInfo.InvariantCulture),
                    ["BackupDir"] = GetBackupDirectory()
                },
                progress: null,
                ct).ConfigureAwait(true);

            HasRestoreManifest = File.Exists(GetManifestPath());
            OnPropertyChanged(nameof(RestoreHint));

            if (result.IsSuccess)
            {
                StatusText = L.T("Элемент отключён. {0}", result.Message);
                _logger.Info("STARTUP | disable ok");
            }
            else
            {
                StatusText = L.T("Не удалось отключить «{0}» (код {1}): {2}", row.Name, result.Code, result.Message);
                _logger.Error($"STARTUP | disable rc={result.Code} | {result.Message}");
            }

            // Пользовательское событие автозагрузки — в историю при любом исходе.
            _history.Enqueue(new HistoryEvent(
                DateTime.Now,
                L.T("Автозагрузка"),
                L.T("Элемент автозагрузки: {0}", row.Name),
                result.IsSuccess ? HistoryEvent.StatusOk : HistoryEvent.StatusFail,
                result.IsSuccess ? L.T("отключена") : result.Message));

            await LoadRowsAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        var confirmed = _dialogs.Ask(
            L.T("Восстановление автозагрузки"),
            L.T("Вернуть ранее отключённые элементы автозагрузки из манифеста SCU.ps1?"),
            L.T("Восстановить"));
        if (!confirmed)
        {
            return;
        }

        await RunExclusiveAsync("восстановление автозагрузки", async ct =>
        {
            StatusText = L.T("Восстановление автозагрузки…");
            _logger.Info("STARTUP | restore | dir=" + GetBackupDirectory());

            var result = await _runner.RunAsync(
                "StartupRestore",
                new Dictionary<string, string?>
                {
                    ["BackupDir"] = GetBackupDirectory()
                },
                progress: null,
                ct).ConfigureAwait(true);

            HasRestoreManifest = File.Exists(GetManifestPath());
            OnPropertyChanged(nameof(RestoreHint));

            if (result.IsSuccess)
            {
                StatusText = L.T("Откат выполнен. {0}", result.Message);
                _logger.Info("STARTUP | restore ok");
            }
            else
            {
                StatusText = L.T("Откат завершился с кодом {0}: {1}", result.Code, result.Message);
                _logger.Error($"STARTUP | restore rc={result.Code} | {result.Message}");
            }

            // Восстановление из манифеста — тоже пользовательское событие истории.
            _history.Enqueue(new HistoryEvent(
                DateTime.Now,
                L.T("Автозагрузка"),
                L.T("Восстановление автозагрузки"),
                result.IsSuccess ? HistoryEvent.StatusOk : HistoryEvent.StatusFail,
                result.Message));

            await LoadRowsAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | startup operation");
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

    private bool CanDisable(StartupItem? row) =>
        !IsBusy && IsAdmin && IsSCUAvailable && row is not null && !IsJunk(row);

    private bool CanRestore() => !IsBusy && IsAdmin && IsSCUAvailable && HasRestoreManifest;

    private bool CanCancel() => IsBusy;

    private async Task<bool> LoadRowsAsync(CancellationToken ct)
    {
        if (!IsSCUAvailable)
        {
            StatusText = L.T("SCU.ps1 недоступен.");
            return false;
        }

        var path = GetListFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? GetCacheDirectory());

        var result = await _runner.RunAsync(
            "StartupList",
            new Dictionary<string, string?>
            {
                ["OutFile"] = path
            },
            progress: null,
            ct).ConfigureAwait(true);

        if (!result.IsSuccess)
        {
            StatusText = L.T("Не удалось прочитать автозагрузку (код {0}): {1}", result.Code, result.Message);
            _logger.Error($"STARTUP | list rc={result.Code} | {result.Message}");
            return false;
        }

        Rows.Clear();
        // Чтение файла списка — IO: не на UI-потоке.
        var items = await TaskRunner.RunBlocking(() => ParseList(path), ct).ConfigureAwait(true);
        foreach (var item in items)
        {
            if (IsJunk(item))
            {
                continue;
            }

            Rows.Add(item);
        }

        DisableCommand.NotifyCanExecuteChanged();
        return true;
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
            _logger.Error("STARTUP | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static IReadOnlyList<StartupItem> ParseList(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var items = new List<StartupItem>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 4)
            {
                continue;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            items.Add(new StartupItem(index, parts[1], parts[2], parts[3]));
        }

        return items;
    }

    internal static bool IsJunk(StartupItem item)
    {
        return string.Equals(item.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Name, "Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU",
            "cache");

    private static string GetListFilePath() => Path.Combine(GetCacheDirectory(), "startup_list.txt");

    private static string GetBackupDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU",
            "backup",
            "startup");

    private static string GetManifestPath() => Path.Combine(GetBackupDirectory(), "manifest.json");
}
