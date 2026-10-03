using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Infrastructure.Updates;
using SCU.Infrastructure.Windows.Drivers;
using SCU.Infrastructure.Windows.Maintenance;
using SCU.Models;
using SCU.Models.Drivers;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Раздел 25 «Драйверы»: инвентарь ключевого оборудования (WMI), онлайн-поиск
// обновлений драйверов через Windows Update API и установка, проблемные
// устройства, точка восстановления перед установкой, экспорт пакетов (pnputil).
// Источник обновлений — только WU (подбор по DeviceID, подпись Microsoft);
// вендорские фиды не используются — см. план раздела.
public partial class DriversViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly IFilePickerService _filePicker;
    private readonly IShellOpenService _shell;
    private readonly HistoryStore _history;
    private readonly DriverInventoryService _inventory = new();
    private readonly WindowsUpdateDriverService _wuService;
    private readonly DriverExportService _exportService;
    private readonly RestorePointService _restorePoints;
    private readonly UpdateService _updateService;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleDriverUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    // Экспорт идёт без глобального оверлея: спиннер стоит прямо у кнопки,
    // поэтому IsBusy (по нему гасится весь экран) при экспорте не поднимается.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleDriverUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isExporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    [NotifyPropertyChangedFor(nameof(ReadOnlyHint))]
    private bool _isAdmin;

    public bool IsInteractive => !IsBusy && IsAdmin;

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — установка и экспорт драйверов недоступны.");

    [ObservableProperty]
    private string _statusText = L.T("Состояние драйверов ещё не проверено.");

    // Сводка карточки «Состояние драйверов».
    [ObservableProperty]
    private string _summaryText = string.Empty;

    // Подсказка о запрете драйверов (ExcludeWUDriversInQualityUpdate) — поиск через API работает.
    [ObservableProperty]
    private string _wuHintText = string.Empty;

    [ObservableProperty]
    private bool _hasUpdates;

    [ObservableProperty]
    private string _updatesHeaderText = string.Empty;

    public ObservableCollection<DriverUpdateRow> AvailableUpdates { get; } = [];

    [ObservableProperty]
    private bool _hasDevices;

    public ObservableCollection<DriverDeviceRow> Devices { get; } = [];

    [ObservableProperty]
    private bool _hasProblems;

    public ObservableCollection<DriverProblemRow> Problems { get; } = [];

    // Точка восстановления (тип 10 «установка драйвера») перед установкой пакета.
    [ObservableProperty]
    private bool _createRestorePoint = true;

    // Разрешить драйверы вместе с обновлениями Windows (инверсия ExcludeWUDriversInQualityUpdate).
    [ObservableProperty]
    private bool _driverUpdatesAllowed = true;

    private bool _isWuBlocked;

    public DriversViewModel(
        Logger logger,
        IConfirmDialogService dialogs,
        IFilePickerService filePicker,
        IShellOpenService shell,
        HistoryStore history)
    {
        _logger = logger;
        _dialogs = dialogs;
        _filePicker = filePicker;
        _shell = shell;
        _history = history;
        _wuService = new WindowsUpdateDriverService(logger);
        _exportService = new DriverExportService(logger);
        _restorePoints = new RestorePointService(logger);
        _updateService = new UpdateService(logger, new RegistryHelper(logger));
        IsAdmin = Elevation.IsAdmin();
    }

    public DriversViewModel(Logger logger, HistoryStore history)
        : this(logger, new ConfirmDialogService(), new FilePickerService(), new ShellOpenService(), history)
    {
    }

    private bool CanRefresh() => !IsBusy && !IsExporting;
    private bool CanSearch() => !IsBusy && !IsExporting;
    private bool CanModify() => !IsBusy && !IsExporting && IsAdmin;
    private bool CanCancel() => IsBusy || IsExporting;
    private bool CanToggleDriverUpdates() => !IsBusy && !IsExporting;
    private bool CanExport() => !IsBusy && !IsExporting && IsAdmin;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("инвентарь драйверов", async ct =>
        {
            StatusText = L.T("Чтение устройств и драйверов…");
            var inventory = await _inventory.GetAsync(ct).ConfigureAwait(true);
            if (!inventory.IsSuccess || inventory.Value is null)
            {
                StatusText = L.T("Ошибка: {0}", inventory.Message);
                return;
            }

            FillDevices(inventory.Value.Devices, inventory.Value.Problems);

            // Состояние тумблера WU и блокировки служб — реестр, не на UI-потоке.
            var state = await TaskRunner.RunBlocking(
                () => (blocked: UpdateService.IsBlocked(), driversExcluded: UpdateService.IsDriverUpdatesExcluded()),
                ct).ConfigureAwait(true);
            _isWuBlocked = state.blocked;
            DriverUpdatesAllowed = !state.driversExcluded;
            UpdateWuHint();

            SummaryText = L.T("Устройств: {0} · Проблемных: {1} · Обновлений: {2}",
                inventory.Value.Devices.Count,
                inventory.Value.Problems.Count,
                AvailableUpdates.Count);
            StatusText = L.T("Список драйверов обновлён. Для поиска свежих версий используйте «Проверить обновления».");
        }).ConfigureAwait(true);
    }

    private void FillDevices(
        IReadOnlyList<DriverDeviceInfo> devices,
        IReadOnlyList<DriverProblemInfo> problems)
    {
        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(new DriverDeviceRow(device));
        }

        HasDevices = Devices.Count > 0;
        Problems.Clear();
        foreach (var problem in problems)
        {
            Problems.Add(new DriverProblemRow(problem));
        }

        HasProblems = Problems.Count > 0;
    }

    // Исходный текст кода ошибки зависит от кода; здесь только самый частый (28).
    private void UpdateWuHint()
    {
        WuHintText = _isWuBlocked
            ? L.T("Службы обновления заблокированы (раздел «Обновления Windows») — поиск недоступен, пока они отключены.")
            : DriverUpdatesAllowed
                ? string.Empty
                : L.T("Авто-установка драйверов через Windows Update запрещена; поиск по запросу продолжает работать.");
        OnPropertyChanged(nameof(HasWuHint));
    }

    public bool HasWuHint => WuHintText.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchUpdatesAsync()
    {
        await RunExclusiveAsync("поиск обновлений драйверов", async ct =>
        {
            if (UpdateService.IsBlocked())
            {
                _isWuBlocked = true;
                UpdateWuHint();
                StatusText = L.T("Поиск недоступен: службы обновления заблокированы. Используйте «Вернуть обновления» в разделе «Обновления Windows».");
                return;
            }

            AvailableUpdates.Clear();
            HasUpdates = false;
            UpdatesHeaderText = string.Empty;
            var result = await _wuService.SearchAsync(new Progress<string>(text => StatusText = text), ct)
                .ConfigureAwait(true);
            if (!result.IsSuccess || result.Value is null)
            {
                StatusText = L.T("Ошибка: {0}", result.Message);
                return;
            }

            foreach (var update in result.Value)
            {
                AvailableUpdates.Add(new DriverUpdateRow(update));
            }

            HasUpdates = AvailableUpdates.Count > 0;
            UpdatesHeaderText = HasUpdates
                ? L.T("Найдено обновлений: {0}", AvailableUpdates.Count)
                : L.T("Обновлений драйверов не найдено — все установлены.");
            SummaryText = L.T("Устройств: {0} · Проблемных: {1} · Обновлений: {2}",
                Devices.Count, Problems.Count, AvailableUpdates.Count);
            StatusText = HasUpdates
                ? L.T("Поиск завершён. Установите нужные пакеты кнопкой «Обновить».")
                : L.T("Поиск завершён.");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task InstallUpdateAsync(DriverUpdateRow? row)
    {
        if (row is not null)
        {
            await InstallAsync([row]).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task InstallAllAsync()
    {
        if (AvailableUpdates.Count == 0)
        {
            return;
        }

        if (!_dialogs.Ask(
                L.T("Обновление всех драйверов"),
                L.T("Будут загружены и установлены все найденные пакеты: {0}.\nПродолжить?", AvailableUpdates.Count),
                L.T("Обновить всё")))
        {
            return;
        }

        await InstallAsync([.. AvailableUpdates]).ConfigureAwait(true);
    }

    private async Task InstallAsync(IReadOnlyList<DriverUpdateRow> rows)
    {
        await RunExclusiveAsync("установка драйверов", async ct =>
        {
            if (CreateRestorePoint)
            {
                StatusText = L.T("Создание точки восстановления…");
                var checkpoint = await _restorePoints.CreateDriverCheckpointAsync(
                    L.T("Обновление драйверов SCU"), ct).ConfigureAwait(true);
                if (!checkpoint.IsSuccess
                    && !_dialogs.Ask(
                        L.T("Точка восстановления"),
                        L.T("Не удалось создать точку восстановления: {0}\nПродолжить установку без неё?", checkpoint.Message),
                        L.T("Продолжить")))
                {
                    StatusText = L.T("Установка отменена.");
                    return;
                }
            }

            foreach (var row in rows)
            {
                row.IsInstalling = true;
            }

            var keys = rows.Select(row => row.Key).ToList();
            var result = await _wuService.InstallAsync(keys, new Progress<string>(text => StatusText = text), ct)
                .ConfigureAwait(true);

            foreach (var row in rows)
            {
                row.IsInstalling = false;
            }

            if (!result.IsSuccess || result.Value is null)
            {
                StatusText = L.T("Ошибка: {0}", result.Message);
                return;
            }

            var summary = result.Value;
            // Установленные пакеты исчезают из списка доступных.
            var succeededKeys = summary.SucceededKeys.ToHashSet();
            foreach (var row in rows.Where(row => succeededKeys.Contains(row.Key)).ToList())
            {
                AvailableUpdates.Remove(row);
            }

            HasUpdates = AvailableUpdates.Count > 0;
            UpdatesHeaderText = HasUpdates
                ? L.T("Найдено обновлений: {0}", AvailableUpdates.Count)
                : L.T("Обновлений драйверов не найдено — все установлены.");
            SummaryText = L.T("Устройств: {0} · Проблемных: {1} · Обновлений: {2}",
                Devices.Count, Problems.Count, AvailableUpdates.Count);

            var succeededCount = rows.Count - summary.Failed;
            StatusText = summary.Failed == 0
                ? L.T("Установлено пакетов: {0}.{1}", succeededCount,
                    summary.RebootRequired ? " " + L.T("Требуется перезагрузка.") : string.Empty)
                : L.T("Установлено: {0}, не удалось: {1}. {2}",
                    succeededCount, summary.Failed, string.Join("; ", summary.Failures));

            _history.Enqueue(new HistoryEvent(
                DateTime.Now,
                L.T("Драйверы"),
                L.T("Обновление драйверов ({0})", rows.Count),
                summary.Failed == 0 ? HistoryEvent.StatusOk : HistoryEvent.StatusFail,
                StatusText));

            AppNotificationCenter.Instance.Push(
                summary.Failed == 0 ? L.T("Драйверы обновлены") : L.T("Драйверы: частичная ошибка"),
                StatusText,
                summary.Failed == 0 ? AppNotificationKind.Success : AppNotificationKind.Warn);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanToggleDriverUpdates))]
    private async Task ToggleDriverUpdatesAsync()
    {
        var allow = DriverUpdatesAllowed;
        await RunExclusiveAsync("драйверы через Windows Update", async ct =>
        {
            StatusText = allow
                ? L.T("Разрешение установки драйверов через Windows Update…")
                : L.T("Запрет авто-драйверов через Windows Update…");
            var result = await TaskRunner.RunBlocking(
                () => _updateService.SetDriverUpdatesExcluded(!allow), ct).ConfigureAwait(true);
            var actualExcluded = await TaskRunner.RunBlocking(UpdateService.IsDriverUpdatesExcluded, ct)
                .ConfigureAwait(true);
            DriverUpdatesAllowed = !actualExcluded;
            UpdateWuHint();
            StatusText = result.IsSuccess
                ? allow
                    ? L.T("Драйверы снова устанавливаются через Windows Update.")
                    : L.T("Авто-установка драйверов через Windows Update запрещена.")
                : L.T("Ошибка: {0}", result.Message);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var targetDirectory = _filePicker.PickOpenFolder(L.T("Папка для экспорта драйверов"));
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return;
        }

        // Свой CTS и IsExporting вместо IsBusy: пнputil пишет статус в карточку,
        // спиннер крутится у кнопки — экран целиком не затемняется.
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        IsExporting = true;
        try
        {
            var result = await _exportService.ExportAsync(
                targetDirectory, new Progress<string>(text => StatusText = text), _operationCts.Token).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                StatusText = L.T("Экспортировано пакетов: {0} → {1}", result.Value, targetDirectory);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Драйверы"),
                    L.T("Экспорт драйверов"),
                    HistoryEvent.StatusOk,
                    targetDirectory));
            }
            else
            {
                StatusText = L.T("Ошибка: {0}", result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = L.T("Операция отменена.");
            _logger.Warn("CANCEL | экспорт драйверов");
        }
        catch (Exception exception)
        {
            StatusText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("DRV | экспорт драйверов | " + exception);
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private void OpenDeviceManager() => _shell.OpenPath("devmgmt.msc");

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | driver operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    public void CancelOngoing() => _operationCts?.Cancel();

    public void Dispose() => _operationCts?.Cancel();

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
            _logger.Error("DRV | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// Строка доступного обновления драйвера.
public sealed partial class DriverUpdateRow : ObservableObject
{
    public DriverUpdateRow(DriverUpdateInfo info)
    {
        Key = info.Key;
        Title = info.Title;
        Details = string.Join(" · ", new[]
            {
                info.Provider,
                info.Version,
                info.DriverDate is { } date ? L.Date(date) : null,
                info.SizeBytes > 0 ? FormatBytes(info.SizeBytes) : null,
            }.Where(part => !string.IsNullOrEmpty(part)));
    }

    public string Key { get; }

    public string Title { get; }

    public string Details { get; }

    [ObservableProperty]
    private bool _isInstalling;

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / (1024 * 1024.0)).ToString("0.#") + " " + L.T("МБ")
            : (bytes / 1024.0).ToString("0") + " " + L.T("КБ");
}

// Строка устройства с установленным драйвером.
public sealed class DriverDeviceRow(DriverDeviceInfo info)
{
    public string Name => info.Name;

    public string ClassText => info.DeviceClass;

    public string Provider => info.Provider;

    public string Version => info.Version;

    public string DateText => info.DriverDate is { } date ? L.Date(date) : "—";

    // Сводка карточки устройств: класс, поставщик, версия и дата — второй строкой под именем.
    public string DetailsText => string.Join(" · ",
        new[]
        {
            info.DeviceClass,
            info.Provider,
            info.Version,
            DateText,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public bool HasProblem => info.HasProblem;
}

// Строка проблемного устройства.
public sealed class DriverProblemRow(DriverProblemInfo info)
{
    public string Name => info.Name;

    public string Manufacturer => info.Manufacturer;

    public string ProblemText => info.ProblemCode == 28
        ? $"{info.Name} — {L.T("драйвер не установлен")}"
        : $"{info.Name} — {L.T("код {0}", info.ProblemCode)}";
}
