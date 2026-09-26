using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Services;
using SCU.Services.Dashboard;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Строка таблицы мусорного ПО.
public sealed class BloatRow : INotifyPropertyChanged
{
    private string _stateText;

    public BloatRow(BloatApp app, string stateText)
    {
        App = app;
        _stateText = stateText;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BloatApp App { get; }

    public string ScanKey => App.ScanKey;

    public string Title => App.Title;

    public string RemoveNames => App.RemoveNames;

    public bool Irreversible => App.Irreversible;

    public string ToolTipText =>
        $"Пакеты: {RemoveNames}\n" +
        (Irreversible
            ? "Удаление необратимо в рамках этой утилиты.\nПереустановка возможна только средствами Windows/Store/winget."
            : "Удаление изменяет состав установленных компонентов; перед операцией приложение показывает подтверждение.");

    public string StateText
    {
        get => _stateText;
        set
        {
            _stateText = value;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(IsRemoved));
            OnPropertyChanged(nameof(IsUnknown));
        }
    }

    // Логика (CanRemove/IsRemoved) держится на канонических русских ключах состояния;
    // на экран идёт перевод через L.T, чтобы En-интерфейс не показывал русский текст.
    public string DisplayText => L.T(_stateText);

    // Удалять можно только то, что подтверждено сканом; у отсутствующих кнопка глушится.
    public bool CanRemove => string.Equals(_stateText, "установлено", StringComparison.OrdinalIgnoreCase);

    // Пакеты уже удалены (подтверждено сканом или скриптом) — вместо кнопки показывается галочка.
    public bool IsRemoved =>
        _stateText.StartsWith("пакеты удалены", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_stateText, "отсутствует", StringComparison.OrdinalIgnoreCase);

    // Состояние ещё неизвестно (скан не выполнялся) — показываем текст состояния.
    public bool IsUnknown => !CanRemove && !IsRemoved;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

// Раздел 4 «Удаление мусорного ПО»: скан через AppxScan, удаление через AppxRemove.
// Скрипт SCU.ps1 сам проверяет отсутствие пакетов (rc=0 = подтверждено).
public partial class BloatViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly BloatService _bloatService;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveAppCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAdmin;

    [ObservableProperty]
    private bool _isScanAvailable;

    [ObservableProperty]
    private string _statusText = L.T("Скан AppX ещё не выполнялся.");

    public bool HasWarning => !IsAdmin || !IsScanAvailable;

    public BloatViewModel(Logger logger, SCURunner runner, IConfirmDialogService dialogs, HistoryStore history)
    {
        _logger = logger;
        _dialogs = dialogs;
        _history = history;
        IsAdmin = Elevation.IsAdmin();
        IsScanAvailable = runner.IsAvailable;
        _bloatService = new BloatService(logger, runner, new LongProcessRunner(logger));

        foreach (var app in _bloatService.Apps)
        {
            Rows.Add(new BloatRow(app, "неизвестно — выполните скан"));
        }

        Rows.Add(new BloatRow(
            new BloatApp("Edge", "Microsoft Edge", "(setup.exe + winget)", Irreversible: true),
            "неизвестно — выполните скан"));
    }

    public ObservableCollection<BloatRow> Rows { get; } = [];

    public async Task InitializeAsync()
    {
        var installed = await TaskRunner.RunBlocking(_bloatService.IsEdgeInstalled).ConfigureAwait(true);
        var edgeRow = Rows.FirstOrDefault(r => string.Equals(r.App.ScanKey, "Edge", StringComparison.OrdinalIgnoreCase));
        if (edgeRow is not null)
        {
            edgeRow.StateText = installed ? "установлено" : "отсутствует";
        }
    }

    public string ReadOnlyHint
    {
        get
        {
            if (!IsScanAvailable)
            {
                return L.T("SCU.ps1 недоступен — скан и удаление заблокированы.");
            }

            return IsAdmin ? string.Empty : L.T("Нужны права администратора — удаление недоступно.");
        }
    }

    private bool CanModify() => !IsBusy && IsAdmin && IsScanAvailable;
    private bool CanRefresh() => !IsBusy && IsScanAvailable;
    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("скан AppX", async ct =>
        {
            StatusText = L.T("Сканирование фактически установленных AppX…");
            var scan = await _bloatService.ScanAsync(ct).ConfigureAwait(true);
            UpdateRows(scan.IsSuccess && scan.Value is not null
                ? scan.Value
                : new AppxScanState(false, new Dictionary<string, string>()));
            StatusText = scan.IsSuccess && scan.Value is not null && scan.Value.Scanned
                ? L.T("Скан завершён. Удаление необратимо: восстановление возможно только переустановкой из Store/winget.")
                : L.T("Полный список AppX не прочитан — удаление заблокировано, статусы не считаются подтверждёнными.");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RemoveAppAsync(BloatRow? row)
    {
        if (row is null)
        {
            return;
        }

        var warning = row.Irreversible
            ? "\n\n" + L.T("ВНИМАНИЕ: операция помечена как НЕОБРАТИМАЯ в рамках этой утилиты.")
            : string.Empty;
        if (!_dialogs.Ask(
                L.T("Удаление — {0}", row.Title),
                L.T("Удалить «{0}»?\nПакеты: {1}{2}", row.Title, row.RemoveNames, warning),
                L.T("Удалить")))
        {
            return;
        }

        await RunExclusiveAsync("удаление " + row.ScanKey, async ct =>
        {
            StatusText = L.T("Удаление ") + L.S(row.Title) + "…";
            var result = row.ScanKey switch
            {
                "Xbox" => await _bloatService.RemoveXboxAsync(ct).ConfigureAwait(true),
                "Store" => await _bloatService.RemoveStoreAsync(ct).ConfigureAwait(true),
                _ => await _bloatService.RemoveAsync(row.App, ct).ConfigureAwait(true)
            };

            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
            _logger.Info($"BLOAT | remove | {row.ScanKey} | rc={result.Code}");
            if (result.IsSuccess)
            {
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Мусорное ПО"),
                    L.T("Удаление: {0}", row.Title),
                    HistoryEvent.StatusOk));
            }
            // Дать системе время применить состояние AppX, иначе рескан видит ещё не удалённый пакет.
            await Task.Delay(2500, ct).ConfigureAwait(true);
            await RescanIntoRowsAsync(ct).ConfigureAwait(true);
            if (row.ScanKey == "Xbox" && result.IsSuccess)
            {
                // Скановый шаблон 'Xbox' ловит несъёмный системный XboxGameCallableUI —
                // показываем факт: удаляемые пакеты отсутствуют (подтверждено скриптом).
                row.StateText = "пакеты удалены (проверено скриптом)";
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RemoveAllAsync()
    {
        if (!_dialogs.Ask(
                L.T("Удалить всё перечисленное"),
                L.T("Будут удалены ВСЕ перечисленные AppX, Microsoft Store и Microsoft Edge.\n\n"
                    + "Операция НЕОБРАТИМА: Store и Edge могут потребоваться другим приложениям,\n"
                    + "а Edge-удаление может затронуть WebView2.\n\nПродолжить?"),
                L.T("Удалить всё")))
        {
            return;
        }

        await RunExclusiveAsync("массовое удаление", async ct =>
        {
            StatusText = L.T("Массовое удаление AppX и Edge… Это может занять несколько минут.");
            var result = await _bloatService.RemoveAllAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess
                ? L.S(result.Message)
                : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
            _logger.Info("BLOAT | remove all | rc=" + result.Code);
            if (result.IsSuccess)
            {
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Мусорное ПО"),
                    L.T("Массовое удаление"),
                    HistoryEvent.StatusOk));
            }
            await RescanIntoRowsAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | bloat operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        _operationCts?.Cancel();
    }

    private async Task RescanIntoRowsAsync(CancellationToken ct)
    {
        var scan = await _bloatService.ScanAsync(ct).ConfigureAwait(true);
        UpdateRows(scan.IsSuccess && scan.Value is not null
            ? scan.Value
            : new AppxScanState(false, new Dictionary<string, string>()));
    }

    private void UpdateRows(AppxScanState scan)
    {
        foreach (var row in Rows)
        {
            row.StateText = scan.StateText(row.ScanKey);
        }

        if (Rows.All(r => r.ScanKey != "Edge"))
        {
            Rows.Add(new BloatRow(
                new BloatApp("Edge", "Microsoft Edge", "(setup.exe + winget)", Irreversible: true),
                _bloatService.IsEdgeInstalled() ? "установлено" : "отсутствует"));
        }
        else
        {
            Rows.First(r => r.ScanKey == "Edge").StateText = _bloatService.IsEdgeInstalled()
                ? "установлено"
                : "отсутствует";
        }
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
            _logger.Error("BLOAT | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
