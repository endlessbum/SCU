using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.ViewModels.Sections;

/// <summary>
/// Раздел 23 «Устранение неполадок»: read-only диагностика → карточки проблем →
/// подтверждённые исправления с повторной проверкой (fix → verify).
/// </summary>
public partial class TroubleshootingViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly HistoryStore _history;
    private readonly TroubleshootingService _service;
    private CancellationTokenSource? _operationCts;

    public TroubleshootingViewModel(
        Logger logger,
        IConfirmDialogService dialogs,
        HistoryStore history,
        LongProcessRunner runner)
    {
        _logger = logger;
        _dialogs = dialogs;
        _history = history;
        _service = new TroubleshootingService(logger, runner);
        IsAdmin = Elevation.IsAdmin();
        StatusText = L.T("Диагностика ещё не выполнялась.");

        // Строки прогресса идут в том же порядке, что probes движка.
        foreach (var probe in _service.Probes)
        {
            ProbeRows.Add(new ProbeStatusRow(L.T(probe.Title)));
        }

        // Группировка карточек: Проблемы / Предупреждения / Информация.
        // Пропущенные состояния не проходят в карточки, но остаются в отдельном
        // представлении для разворачиваемого списка возврата.
        FindingsView = new ListCollectionView(Findings)
        {
            Filter = static item => item is DiagnosticFinding { IsSkipped: false },
            GroupDescriptions = { new PropertyGroupDescription(nameof(DiagnosticFinding.GroupTitle)) }
        };
        SkippedView = new ListCollectionView(Findings)
        {
            Filter = static item => item is DiagnosticFinding { IsSkipped: true }
        };
    }

    public event Action<int>? NavigateToSectionRequested;

    public ObservableCollection<ProbeStatusRow> ProbeRows { get; } = new();

    public ObservableCollection<DiagnosticFinding> Findings { get; } = new();

    public ICollectionView FindingsView { get; }

    // Пропущенные состояния — тот же источник, другое представление фильтром.
    public ICollectionView SkippedView { get; }

    // Пропуски живут по Id: находки пересобираются после каждого fix → verify,
    // Id правил стабильны («services.stopped.wuauserv»), поэтому скрытое
    // состояние остаётся скрытым и после повторной проверки.
    private readonly HashSet<string> _skippedIds = new();

    [ObservableProperty]
    private bool isAdmin;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool hasRun;

    [ObservableProperty]
    private DateTime? lastScanTime;

    public string LastScanTimeText => LastScanTime is null
        ? L.T("—")
        : LastScanTime.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    partial void OnLastScanTimeChanged(DateTime? value) => OnPropertyChanged(nameof(LastScanTimeText));

    // ===================== Сводка =====================

    public int ProblemsCount { get; private set; }

    public int WarningsCount { get; private set; }

    public int InfosCount { get; private set; }

    public int CompletedChecks { get; private set; }

    public int TotalChecks => ProbeRows.Count;

    public bool IsClean => HasRun && ProblemsCount == 0 && WarningsCount == 0;

    // Приоритет цвета прочерка сводки: проблема > предупреждение > информация.
    public bool HasProblems => ProblemsCount > 0;

    public bool HasWarnings => WarningsCount > 0;

    public bool HasInfos => InfosCount > 0;

    public string SummaryTitle => !HasRun
        ? string.Empty
        : IsClean
            ? L.T("Серьёзных проблем не обнаружено")
            : L.T("Найдено {0} состояний, требующих внимания", ProblemsCount + WarningsCount);

    public string SummaryDetail
    {
        get
        {
            if (!HasRun)
            {
                return string.Empty;
            }

            var checks = L.T("Проверено областей: {0} / {1}.", CompletedChecks, TotalChecks);
            return IsClean
                ? checks
                : checks + " " + L.T("Проблем: {0}, предупреждений: {1}, информации: {2}.", ProblemsCount, WarningsCount, InfosCount);
        }
    }

    private void UpdateSummary()
    {
        // Пропущенные состояния из сводки и карточек исключаются.
        ProblemsCount = Findings.Count(finding => !finding.IsSkipped && finding.Severity == DiagnosticSeverity.Critical);
        WarningsCount = Findings.Count(finding => !finding.IsSkipped && finding.Severity == DiagnosticSeverity.Warning);
        InfosCount = Findings.Count(finding => !finding.IsSkipped && finding.Severity == DiagnosticSeverity.Info);
        CompletedChecks = ProbeRows.Count(row => row.State is ProbeState.Ok or ProbeState.Failed);

        OnPropertyChanged(nameof(ProblemsCount));
        OnPropertyChanged(nameof(WarningsCount));
        OnPropertyChanged(nameof(InfosCount));
        OnPropertyChanged(nameof(CompletedChecks));
        OnPropertyChanged(nameof(TotalChecks));
        OnPropertyChanged(nameof(IsClean));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasInfos));
        OnPropertyChanged(nameof(SummaryTitle));
        OnPropertyChanged(nameof(SummaryDetail));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(SkippedHeaderText));
    }

    // ===================== Пропуск и возврат состояний =====================

    public int SkippedCount => Findings.Count(finding => finding.IsSkipped);

    public bool HasSkipped => SkippedCount > 0;

    public string SkippedHeaderText => L.T("Пропущенные состояния ({0})", SkippedCount);

    [RelayCommand]
    private void Skip(DiagnosticFinding? finding)
    {
        if (finding is null || finding.IsSkipped)
        {
            return;
        }

        finding.IsSkipped = true;
        _skippedIds.Add(finding.Id);
        _logger.Info("TROUBLESHOOT | SKIP | " + finding.Id);
        RefreshFindingViews();
        UpdateSummary();
        StatusText = L.T("Состояние «{0}» скрыто — вернуть можно в списке пропущенных.", finding.Title);
    }

    [RelayCommand]
    private void Restore(DiagnosticFinding? finding)
    {
        if (finding is null || !finding.IsSkipped)
        {
            return;
        }

        finding.IsSkipped = false;
        _skippedIds.Remove(finding.Id);
        _logger.Info("TROUBLESHOOT | RESTORE | " + finding.Id);
        RefreshFindingViews();
        UpdateSummary();
        StatusText = L.T("Состояние «{0}» возвращено.", finding.Title);
    }

    // Фильтры представлений не следят за свойствами элементов — после смены
    // флага IsSkipped представления перестраиваются вручную.
    private void RefreshFindingViews()
    {
        FindingsView.Refresh();
        SkippedView.Refresh();
    }

    // Пересборка находок после запуска или fix → verify с сохранением пропусков.
    private void ReplaceFindings(IEnumerable<DiagnosticFinding> findings)
    {
        Findings.Clear();
        foreach (var finding in findings)
        {
            finding.IsSkipped = _skippedIds.Contains(finding.Id);
            Findings.Add(finding);
        }

        RefreshFindingViews();
    }

    // ===================== Команды =====================

    private bool CanRun() => !IsBusy;

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        await RunExclusiveAsync(async ct =>
        {
            _logger.Info("TROUBLESHOOT | RUN | started");
            StatusText = L.T("Сбор диагностических данных…");
            foreach (var row in ProbeRows)
            {
                row.State = ProbeState.Pending;
            }

            Findings.Clear();
            HasRun = false;
            UpdateSummary();

            var findings = await _service.RunFullAsync(ProbeRows, ct).ConfigureAwait(true);
            ReplaceFindings(findings);

            HasRun = true;
            LastScanTime = DateTime.Now;
            UpdateSummary();

            // После отмены сохраняем уже собранные результаты: «Проверено 8 / 12».
            StatusText = ct.IsCancellationRequested
                ? L.T("Проверка остановлена. Проверено областей: {0} / {1}.", CompletedChecks, TotalChecks)
                : L.T("Диагностика завершена. {0}", SummaryTitle);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("TROUBLESHOOT | cancel requested");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    [RelayCommand]
    private async Task RunActionAsync(DiagnosticAction? action)
    {
        if (action is null)
        {
            return;
        }

        switch (action.Kind)
        {
            case DiagnosticActionKind.OpenSection when action.SectionNumber is { } number:
                _logger.Info("TROUBLESHOOT | NAV | section=" + number);
                NavigateToSectionRequested?.Invoke(number);
                return;

            case DiagnosticActionKind.OpenApp when action.AppTarget is { } target:
                try
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    _logger.Info("TROUBLESHOOT | OPEN | " + target);
                }
                catch (Exception exception)
                {
                    StatusText = L.T("Не удалось открыть {0}: {1}", target, exception.Message);
                    _logger.Error("TROUBLESHOOT | OPEN | " + target + " | " + exception.Message);
                }

                return;

            case DiagnosticActionKind.RunFix:
                await RunFixAsync(action).ConfigureAwait(true);
                return;
        }
    }

    // confirm → execute → verify → update findings → log/history (п. 18 плана).
    private async Task RunFixAsync(DiagnosticAction action)
    {
        if (!IsAdmin)
        {
            StatusText = L.T("Нужны права администратора — исправление недоступно.");
            return;
        }

        if (action.RequiresConfirm && !_dialogs.Ask(
                action.ConfirmTitle ?? L.T("Подтверждение исправления"),
                action.ConfirmMessage ?? L.T("Выполнить «{0}»?", action.Title),
                action.Title))
        {
            return;
        }

        var beforeIds = Findings.Select(finding => finding.Id).ToHashSet();
        await RunExclusiveAsync(async ct =>
        {
            StatusText = L.T("Выполнение: {0}…", action.Title);
            var result = await _service.RunFixAsync(action, ct).ConfigureAwait(true);
            _history.Enqueue(new HistoryEvent(
                DateTime.Now,
                L.T("Устранение неполадок"),
                action.Title,
                result.IsSuccess ? HistoryEvent.StatusOk : HistoryEvent.StatusFail,
                L.S(result.Message)));

            if (!result.IsSuccess)
            {
                StatusText = L.T("Исправление не выполнено: {0}", result.Message);
                return;
            }

            // «Исправил → перепроверил»: пересбор находок по свежему состоянию.
            StatusText = L.T("Повторная проверка…");
            if (action.VerifyProbeIds is { Length: > 0 })
            {
                var updated = await _service
                    .RecheckAsync(action.VerifyProbeIds, ProbeRows, ct)
                    .ConfigureAwait(true);
                ReplaceFindings(updated);

                UpdateSummary();
            }

            var resolved = beforeIds.Count(id => Findings.All(finding => finding.Id != id));
            StatusText = resolved > 0
                ? L.T("Готово. Проверка подтвердила устранение: {0}", result.Message)
                : L.T("Готово. {0}", result.Message);
        }).ConfigureAwait(true);
    }

    public void CancelOngoing() => _operationCts?.Cancel();

    public void Dispose() => _operationCts?.Cancel();

    private async Task RunExclusiveAsync(Func<CancellationToken, Task> action)
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
            _logger.Warn("CANCEL | troubleshooting operation");
        }
        catch (Exception exception)
        {
            StatusText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("TROUBLESHOOT | " + exception);
        }
        finally
        {
            IsBusy = false;
            UpdateSummary();
        }
    }
}
