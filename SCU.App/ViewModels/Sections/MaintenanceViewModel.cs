using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Диск в списке индексации. IsIndexingDisabled — фактическое состояние
// (атрибут «не индексировать» на корне), IsSelected — желаемое: галочка стоит,
// пока индексация должна быть выключена. При загрузке списка и после «Применить»
// оба синхронизируются с фактом, поэтому «Применить» активно только при изменениях.
public sealed class DriveRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isIndexingDisabled;

    public DriveRow(string letter, bool indexingDisabled)
    {
        Letter = letter;
        _isIndexingDisabled = indexingDisabled;
        _isSelected = indexingDisabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Letter { get; }

    public string Display => Letter + "\\";

    // Фактическое состояние на корне диска; обновляется после применения.
    public bool IsIndexingDisabled
    {
        get => _isIndexingDisabled;
        set
        {
            _isIndexingDisabled = value;
            OnPropertyChanged(nameof(IsIndexingDisabled));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

// Раздел 12 «Поиск и целостность»: индексация, DISM+SFC, точки восстановления,
// глубокая очистка (WinSxS, Delivery Optimization, дампы/WER) и CompactOS.
public partial class MaintenanceViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly MaintenanceService _maintenanceService;
    private readonly RestorePointService _restorePointService;
    private readonly FileCleanupService _cleanupService;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunIntegrityCheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateRestorePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreRestorePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRestorePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeComponentStoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartComponentCleanupCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartComponentCleanupResetBaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearDeliveryOptimizationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearDumpsAndWerCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCompactOsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isAdmin;

    public bool IsInteractive => !IsBusy && IsAdmin;

    [ObservableProperty]
    private string _statusText = L.T("Загрузка состояния…");

    [ObservableProperty]
    private string _componentStoreText = L.T("Хранилище компонентов ещё не анализировалось.");

    // П.17: флаги запуска конкретных операций. «Отмена» каждой строки активна только
    // пока её операция выполняется (раньше одна «Отмена» реагировала на любой busy).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelIntegrityCommand))]
    private bool _isIntegrityRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCleanupCommand))]
    private bool _isComponentCleanupRunning;

    public ObservableCollection<DriveRow> Drives { get; } = [];

    // П.15: список точек восстановления системной защиты (новейшие сверху).
    // Выделения нет: все действия — кнопками в каждой строке.
    public ObservableCollection<RestorePointRow> RestorePoints { get; } = [];

    // Постоянное описание CompactOS — не зависит от ON/OFF, без «Включить:/Отключить:».
    private bool _compactOsEnabled;

    public bool CompactOsEnabled
    {
        get => _compactOsEnabled;
        set
        {
            _compactOsEnabled = value;
            OnPropertyChanged(nameof(CompactOsEnabled));
        }
    }

    public string CompactOsActionText =>
        System.Windows.Application.Current?.TryFindResource("S_MaintCompactOsDesc") as string ?? string.Empty;

    private void RefreshCompactOsActionText() => OnPropertyChanged(nameof(CompactOsActionText));

    public MaintenanceViewModel(Logger logger, LongProcessRunner runner, IConfirmDialogService dialogs, HistoryStore history)
    {
        _logger = logger;
        _dialogs = dialogs;
        _history = history;
        IsAdmin = Elevation.IsAdmin();
        _maintenanceService = new MaintenanceService(logger, runner);
        _restorePointService = new RestorePointService(logger);
        _cleanupService = new FileCleanupService(logger);
        // Подпись CompactOS перечитывается при смене языка (живёт столько же, сколько приложение).
        L.LanguageChanged += RefreshCompactOsActionText;
    }

    private bool CanModify() => !IsBusy && IsAdmin;
    private bool CanRefresh() => !IsBusy;

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — операции раздела недоступны.");

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление раздела", async ct =>
        {
            StatusText = L.T("Чтение состояния (диски, CompactOS, точки восстановления)…");

            var drives = await _maintenanceService.GetDrivesAsync(ct).ConfigureAwait(true);
            var indexingStates = await _maintenanceService.GetIndexingDisabledAsync(drives, ct).ConfigureAwait(true);
            Drives.Clear();
            foreach (var drive in drives)
            {
                var row = new DriveRow(drive, indexingStates.GetValueOrDefault(drive));
                // Переключение галочки меняет доступность «Применить».
                row.PropertyChanged += OnDriveRowChanged;
                Drives.Add(row);
            }

            var compact = await _maintenanceService.GetCompactOsEnabledAsync(ct).ConfigureAwait(true);
            CompactOsEnabled = compact.IsSuccess && compact.Value;

            await RefreshRestorePointsAsync(ct).ConfigureAwait(true);

            StatusText = L.T("Состояние обновлено.");
        }).ConfigureAwait(true);
    }

    // Есть ли изменения, которые ещё не применены: галочка расходится с фактом.
    private bool HasIndexingChanges => Drives.Any(d => d.IsSelected != d.IsIndexingDisabled);

    private bool CanApplyIndexing() => CanModify() && HasIndexingChanges;

    private void OnDriveRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DriveRow.IsSelected))
        {
            ApplyIndexingCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyIndexing))]
    private async Task ApplyIndexingAsync()
    {
        var toDisable = Drives.Where(d => d.IsSelected && !d.IsIndexingDisabled).Select(d => d.Letter).ToList();
        var toEnable = Drives.Where(d => !d.IsSelected && d.IsIndexingDisabled).Select(d => d.Letter).ToList();
        if (toDisable.Count == 0 && toEnable.Count == 0)
        {
            return;
        }

        if (!_dialogs.Ask(
                L.T("Изменение индексации поиска"),
                BuildIndexingConfirmText(toDisable, toEnable),
                L.T("Применить")))
        {
            return;
        }

        await RunExclusiveAsync("изменение индексации", async ct =>
        {
            StatusText = L.T("Изменение атрибутов индексации на дисках…");
            var result = Result.Success();
            if (toDisable.Count > 0)
            {
                result = await _maintenanceService.SetIndexingAsync(toDisable, notIndexed: true, ct).ConfigureAwait(true);
            }

            if (result.IsSuccess && toEnable.Count > 0)
            {
                result = await _maintenanceService.SetIndexingAsync(toEnable, notIndexed: false, ct).ConfigureAwait(true);
            }

            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);

            // Галочки обязаны показать правду: перечитываем фактическое состояние
            // и обновляем оба поля — иначе HasIndexingChanges будет сравнивать
            // свежие галочки со старым снимком факта.
            var drives = Drives.Select(d => d.Letter).ToList();
            var states = await _maintenanceService.GetIndexingDisabledAsync(drives, ct).ConfigureAwait(true);
            foreach (var row in Drives)
            {
                row.IsIndexingDisabled = states.GetValueOrDefault(row.Letter);
                row.IsSelected = states.GetValueOrDefault(row.Letter);
            }
        }).ConfigureAwait(true);
    }

    private string BuildIndexingConfirmText(List<string> toDisable, List<string> toEnable)
    {
        var parts = new List<string>();
        if (toDisable.Count > 0)
        {
            parts.Add(L.T("Пометить как «не индексировать»: {0}", string.Join(", ", toDisable)));
        }

        if (toEnable.Count > 0)
        {
            parts.Add(L.T("Включить индексацию (снять пометку): {0}", string.Join(", ", toEnable)));
        }

        return string.Join("\n", parts) + "\n\n"
            + L.T("Служба Windows Search полностью не отключается. Применить изменения?");
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RunIntegrityCheckAsync()
    {
        if (!_dialogs.Ask(
                L.T("Проверка целостности Windows"),
                L.T("Будут выполнены последовательно:\n1) DISM /Online /Cleanup-Image /RestoreHealth\n2) sfc /scannow\n\n"
                    + "Операция может занять от 10 минут до часа. Прерывать не рекомендуется.\nЗапустить?"),
                L.T("Запустить DISM + SFC")))
        {
            return;
        }

        await RunExclusiveAsync("DISM + SFC", async ct =>
        {
            // П.17а: «Отмена» этой строки активна только пока DISM+SFC выполняется.
            IsIntegrityRunning = true;
            try
            {
                StatusText = L.T("Шаг 1 из 2: DISM /RestoreHealth… (вывод — в журнале ниже)");
                var result = await _maintenanceService.RunIntegrityCheckAsync(ct).ConfigureAwait(true);
                StatusText = result.IsSuccess
                    ? L.S(result.Message)
                    : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
            }
            finally
            {
                IsIntegrityRunning = false;
            }
        }).ConfigureAwait(true);
    }

    // ===================== Точки восстановления =====================

    // Перечитывание списка точек. П.15а: вызывается сразу после создания — точка
    // появляется в списке без ожидания очередного refresh раздела. WMI иногда
    // отдаёт новую точку с задержкой — до трёх попыток с паузой 750 мс.
    private async Task RefreshRestorePointsAsync(CancellationToken ct)
    {
        Result<IReadOnlyList<RestorePointService.RestorePointInfo>> points;
        for (var attempt = 1; ; attempt++)
        {
            points = await _restorePointService.GetRestorePointsAsync(ct).ConfigureAwait(true);
            if (!points.IsSuccess || points.Value!.Count > 0 || attempt >= 3)
            {
                break;
            }

            await Task.Delay(750, ct).ConfigureAwait(true);
        }

        RestorePoints.Clear();
        if (!points.IsSuccess)
        {
            // Раньше ошибка чтения выглядела как «точек нет» — показываем причину.
            StatusText = L.T("Не удалось прочитать список точек: {0}", points.Message);
            return;
        }

        foreach (var point in points.Value ?? [])
        {
            RestorePoints.Add(new RestorePointRow(point));
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task CreateRestorePointAsync()
    {
        if (!_dialogs.Ask(
                L.T("Точка восстановления"),
                L.T("Создать точку восстановления системной защиты (тип «изменение настроек»)?\n"
                + "Отличная страховка перед глубокими изменениями: службы, автозагрузка, реестр."),
                L.T("Создать точку")))
        {
            return;
        }

        await RunExclusiveAsync("создание точки восстановления", async ct =>
        {
            StatusText = L.T("Создание точки восстановления… (до минуты)");
            var description = "SCU " + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            var result = await _restorePointService.CreateCheckpointWithIdAsync(description, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);

            // Событие о точке восстановления — в историю при любом исходе. BackupId
            // заполняется только надёжным идентификатором ("RP-<seq>"); если WMI-списки
            // точек не читались, остаётся null — выдумывать идентификатор нельзя.
            _history.Enqueue(new HistoryEvent(
                DateTime.Now,
                L.T("Резервная копия"),
                L.T("Точка восстановления"),
                result.IsSuccess ? HistoryEvent.StatusOk : HistoryEvent.StatusFail,
                description,
                BackupId: result.Value));

            // П.15а: список обновляется сразу после создания (с retry внутри).
            await RefreshRestorePointsAsync(ct).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    // П.15б: откат Windows SystemRestore к точке из строки списка — с подтверждением.
    [RelayCommand(CanExecute = nameof(CanRestorePoint))]
    private async Task RestoreRestorePointAsync(RestorePointRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!_dialogs.Ask(
                L.T("Восстановление из точки"),
                L.T("Вернуть систему в состояние точки восстановления?\n\n{0}\n\n"
                    + "Windows инициирует откат и перезагрузит компьютер: открытые программы могут быть закрыты, изменения в реестре и системных файлах после точки будут отменены. Личные файлы не затрагиваются. Продолжить?",
                    row.Display),
                L.T("Восстановить")))
        {
            return;
        }

        await RunExclusiveAsync("восстановление из точки восстановления", async ct =>
        {
            StatusText = L.T("Запуск отката Windows SystemRestore…");
            var result = await _restorePointService.RestoreAsync(row.SequenceNumber, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            _logger.Info("RP | restore command | seq=" + row.SequenceNumber + " | rc=" + result.Code);
        }).ConfigureAwait(true);
    }

    private bool CanRestorePoint(RestorePointRow? row) => CanModify() && row is not null;

    // Удаление точки из системного хранилища (srclient) — кнопкой в строке списка.
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DeleteRestorePointAsync(RestorePointRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!_dialogs.Ask(
                L.T("Удаление точки"),
                L.T("Удалить точку восстановления из системного хранилища?\n\n{0}\n\n"
                    + "Точка перестанет отображаться в списке и станет недоступна для восстановления. Продолжить?",
                    row.Display),
                L.T("Удалить")))
        {
            return;
        }

        await RunExclusiveAsync("удаление точки восстановления", async ct =>
        {
            StatusText = L.T("Удаление точки восстановления…");
            var result = await _restorePointService.DeleteAsync(row.SequenceNumber, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            _logger.Info("RP | delete command | seq=" + row.SequenceNumber + " | rc=" + result.Code);
            if (result.IsSuccess)
            {
                await RefreshRestorePointsAsync(ct).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    // ===================== Глубокая очистка =====================

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AnalyzeComponentStoreAsync()
    {
        await RunExclusiveAsync("анализ хранилища компонентов", async ct =>
        {
            // П.17б: «Отмена» в карточке WinSxS активна и для «Анализ», не только для «Очистить».
            IsComponentCleanupRunning = true;
            try
            {
                StatusText = L.T("DISM /AnalyzeComponentStore… (до нескольких минут)");
                var result = await _maintenanceService.AnalyzeComponentStoreAsync(ct).ConfigureAwait(true);
                ComponentStoreText = result.IsSuccess
                    ? result.Value ?? string.Empty
                    : L.T("Ошибка: {0}", result.Message);
                StatusText = result.IsSuccess ? L.T("Анализ завершён.") : L.T("Анализ не удался — см. карточку.");
            }
            finally
            {
                IsComponentCleanupRunning = false;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task StartComponentCleanupAsync() => RunComponentCleanupAsync(resetBase: false);

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task StartComponentCleanupResetBaseAsync() => RunComponentCleanupAsync(resetBase: true);

    private async Task RunComponentCleanupAsync(bool resetBase)
    {
        if (resetBase)
        {
            if (!_dialogs.Ask(
                    L.T("Очистка хранилища компонентов с /ResetBase"),
                    L.T("DISM удалит все заменённые версии компонентов и заменит текущие.\n\n"
                        + "После /ResetBase установленные обновления Windows БОЛЬШЕ НЕЛЬЗЯ УДАЛИТЬ "
                        + "(не будет работать «Удалить обновление»).\n"
                        + "Точка восстановления перед операцией — хорошая идея. Запустить?"),
                    L.T("Очистить с ResetBase")))
            {
                return;
            }
        }
        else
        {
            if (!_dialogs.Ask(
                    L.T("Очистка хранилища компонентов"),
                    L.T("DISM /StartComponentCleanup удалит заменённые версии компонентов WinSxS.\n"
                        + "Операция долгая (10–40 минут), не прерывайте без необходимости.\nЗапустить?"),
                    L.T("Очистить")))
            {
                return;
            }
        }

        await RunExclusiveAsync(resetBase ? "WinSxS cleanup + ResetBase" : "WinSxS cleanup", async ct =>
        {
            // П.17б: «Отмена» WinSxS активна только после запуска «Анализ»/«Очистить».
            IsComponentCleanupRunning = true;
            try
            {
                StatusText = L.T("DISM /StartComponentCleanup… (вывод — в журнале ниже)");
                var result = await _maintenanceService.StartComponentCleanupAsync(resetBase, ct).ConfigureAwait(true);
                StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
                if (result.IsSuccess)
                {
                    var analysis = await _maintenanceService.AnalyzeComponentStoreAsync(ct).ConfigureAwait(true);
                    ComponentStoreText = analysis.IsSuccess
                        ? L.T("Очистка выполнена. {0}", analysis.Value ?? string.Empty)
                        : L.T("Очистка выполнена. Повторный анализ не удался: {0}", analysis.Message);
                }
            }
            finally
            {
                IsComponentCleanupRunning = false;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ClearDeliveryOptimizationAsync()
    {
        await RunExclusiveAsync("очистка кэша Delivery Optimization", async ct =>
        {
            StatusText = L.T("Очистка кэша Delivery Optimization…");
            var result = await _maintenanceService.ClearDeliveryOptimizationCacheAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ClearDumpsAndWerAsync()
    {
        await RunExclusiveAsync("очистка дампов и WER", async ct =>
        {
            StatusText = L.T("Очистка дампов памяти и очереди отчётов об ошибках…");
            var progress = new Progress<string>(line => StatusText = L.S(line));
            var result = await _cleanupService
                .CleanAsync(MaintenanceService.GetDeepCleanTargets(), progress, ct)
                .ConfigureAwait(true);
            StatusText = result.IsSuccess
                ? L.T("Дампы и WER: {0}", string.Join("; ", result.Value?.Select(FormatCleanupItem) ?? []))
                : L.T("Ошибка: {0}", result.Message);
        }).ConfigureAwait(true);
    }

    private static string FormatCleanupItem(CleanupItemResult item) =>
        L.T("{0}: удалено {1} / {2}", item.Label, item.FilesDeleted, item.FilesBefore);

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleCompactOsAsync()
    {
        var enable = CompactOsEnabled;
        await RunExclusiveAsync("CompactOS", async ct =>
        {
            StatusText = L.T(enable ? "Сжатие системных файлов (compact /always)…" : "Распаковка системных файлов…");
            try
            {
                var result = await _maintenanceService.SetCompactOsAsync(enable, ct).ConfigureAwait(true);
                // Перечитываем фактическое состояние: тумблер обязан показать правду.
                var state = await _maintenanceService.GetCompactOsEnabledAsync(ct).ConfigureAwait(true);
                CompactOsEnabled = state.IsSuccess && state.Value;
                StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            }
            catch (OperationCanceledException)
            {
                // Ресинк на пути отмены: чтение без токена, сбои чтения глотаются.
                try
                {
                    var state = await _maintenanceService.GetCompactOsEnabledAsync(CancellationToken.None).ConfigureAwait(true);
                    CompactOsEnabled = state.IsSuccess && state.Value;
                }
                catch
                {
                    // Не маскируем исходную отмену ошибкой чтения.
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    // П.17: отдельные отмены строк «Проверка целостности» и «Хранилище компонентов».
    // Каждая активна только пока выполняется её операция; обе прерывают общий токен
    // раздела (у раздела одна операция одновременно).
    [RelayCommand(CanExecute = nameof(CanCancelIntegrity))]
    private void CancelIntegrity()
    {
        _logger.Warn("CANCEL | maintenance DISM + SFC");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    private bool CanCancelIntegrity() => IsIntegrityRunning;

    [RelayCommand(CanExecute = nameof(CanCancelCleanup))]
    private void CancelCleanup()
    {
        _logger.Warn("CANCEL | maintenance WinSxS cleanup");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    private bool CanCancelCleanup() => IsComponentCleanupRunning;

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        L.LanguageChanged -= RefreshCompactOsActionText;
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
            _logger.Error("MAINT | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// Строка списка точек восстановления: SequenceNumber для отката, экранное описание —
// «дата | тип | описание» (как в прежнем текстовом списке, но выбираемое).
public sealed class RestorePointRow
{
    public RestorePointRow(RestorePointService.RestorePointInfo info)
    {
        SequenceNumber = info.SequenceNumber;
        // Дата создания — отдельным полем: в строке списка показывается своя колонка.
        CreatedText = info.CreationTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        Type = info.Type;
        Description = info.Description;
        // Собранная строка — для текста подтверждений (восстановление/удаление).
        Display = CreatedText + " | " + Type + " | " + Description;
    }

    public int SequenceNumber { get; }

    public string CreatedText { get; }

    public string Type { get; }

    public string Description { get; }

    public string Display { get; }
}
