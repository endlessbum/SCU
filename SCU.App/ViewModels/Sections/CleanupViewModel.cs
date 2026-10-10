using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

public partial class CleanupViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private const string UpdateServicesCsv = "wuauserv,bits,cryptsvc,msiserver";

    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly SCURunner _runner;
    private readonly FileCleanupService _cleanupService;
    private readonly ServiceManager _serviceManager;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanRecycleBinCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanDownloadsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanTempCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanBrowsersCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanUpdateCacheCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = L.T("Выберите операцию очистки.");

    // Актуальный объём, доступный для очистки, — слева от кнопки «Очистить».
    // Пока идёт подсчёт, вместо объёма крутится индикатор (IsMeasuring*).
    [ObservableProperty]
    private string _recycleBinSizeText = string.Empty;

    [ObservableProperty]
    private bool _isMeasuringRecycleBin;

    // Загрузки — пользовательские файлы (не кэш): очистка только по явному
    // подтверждению с предупреждением о необратимости.
    [ObservableProperty]
    private string _downloadsSizeText = string.Empty;

    [ObservableProperty]
    private bool _isMeasuringDownloads;

    [ObservableProperty]
    private string _tempSizeText = string.Empty;

    [ObservableProperty]
    private bool _isMeasuringTemp;

    [ObservableProperty]
    private string _browsersSizeText = string.Empty;

    [ObservableProperty]
    private bool _isMeasuringBrowsers;

    [ObservableProperty]
    private string _updateCacheSizeText = string.Empty;

    [ObservableProperty]
    private bool _isMeasuringUpdateCache;

    public CleanupViewModel(
        Logger logger,
        SCURunner runner,
        FileCleanupService cleanupService,
        ServiceManager serviceManager,
        IConfirmDialogService dialogs,
        HistoryStore history)
    {
        _logger = logger;
        _dialogs = dialogs;
        _runner = runner;
        _cleanupService = cleanupService;
        _serviceManager = serviceManager;
        _history = history;
        IsAdmin = Elevation.IsAdmin();
        IsSCUAvailable = runner.IsAvailable;
    }

    [ObservableProperty]
    private bool isAdmin;

    [ObservableProperty]
    private bool isSCUAvailable;

    public string ReadOnlyHint
    {
        get
        {
            if (!IsSCUAvailable)
            {
                return L.T("SCU.ps1 недоступен: очистка корзины и кэша обновлений невозможна.");
            }

            if (!IsAdmin)
            {
                return L.T("Нужны права администратора — операции очистки недоступны.");
            }

            return string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanRecycleBinAsync()
    {
        if (!Confirm(L.T("Очистка корзины"), L.T("Безвозвратно очистить корзину на всех дисках?"), L.T("Очистить")))
        {
            return;
        }

        await RunExclusiveAsync("очистка корзины", async ct =>
        {
            StatusText = L.T("Очистка корзины…");
            _logger.Info("CLEAN | recycle bin | start");
            IsMeasuringRecycleBin = true;

            var result = await _runner.RunAsync("EmptyRecycleBin", null, null, ct).ConfigureAwait(true);            // rc=1 у скрипта — «частичный сбой»: корзина очищена, но отдельные системные
            // элементы ($RECYCLE.BIN, занятые файлы) пропущены. Пользователю это не ошибка.
            StatusText = result.Code switch
            {
                0 => L.T("Корзина очищена."),
                1 => L.T("Корзина очищена; отдельные системные или занятые элементы пропущены."),
                _ => L.T("Корзина не очищена (код {0}): {1}", result.Code, result.Message)
            };

            if (result.Code is 0 or 1)
            {
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Очистка"),
                    L.T("Корзина"),
                    HistoryEvent.StatusOk,
                    result.Message));
            }

            RefreshRecycleBinSize();
        }).ConfigureAwait(true);
    }

    // Загрузки — пользовательские файлы: подтверждение прямо предупреждает,
    // что это не временные данные и восстановление невозможно. Задана
    // дополнительная папка («Настройки» → «Путь очистки загрузок») — она
    // перечислена в подтверждении явно.
    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanDownloadsAsync()
    {
        var targets = FileCleanupService.GetDownloadsTargets();
        var extra = targets.FirstOrDefault(target => target.Label == "Загрузки (доп. папка)");
        var message = extra is null
            ? L.T("Будут удалены ВСЕ файлы и папки из «Загрузок» (Downloads).\nЭто пользовательские файлы, а не временные данные — восстановление невозможно.")
            : L.T("Будут удалены ВСЕ файлы и папки из «Загрузок» (Downloads)\nи из дополнительной папки:\n{0}\nЭто пользовательские файлы, а не временные данные — восстановление невозможно.", extra.Directory);
        if (!Confirm(L.T("Очистка папки Загрузки"),
                message,
                L.T("Очистить")))
        {
            return;
        }

        await RunExclusiveAsync("очистка Загрузок", async ct =>
        {
            StatusText = L.T("Очистка Загрузок…");
            IsMeasuringDownloads = true;
            var progress = new Progress<string>(line => StatusText = L.S(line));
            var result = await _cleanupService
                .CleanAsync(targets, progress, ct)
                .ConfigureAwait(true);
            StatusText = FormatCleanupResult(result);
            if (result.IsSuccess && result.Value is { Count: > 0 })
            {
                // BytesFreed — реально измеренные байты по целям, которые существовали.
                var bytesFreed = result.Value.Where(item => item.Existed).Sum(item => item.BytesDeleted);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Очистка"),
                    L.T("Загрузки"),
                    HistoryEvent.StatusOk,
                    FormatCleanupResult(result),
                    bytesFreed));
            }

            RefreshDownloadsSize();
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanTempAsync()
    {
        if (!Confirm(L.T("Очистка папок Temp"),
                L.T("Удалить временные файлы (TEMP, Windows\\Temp, Prefetch, Recent, INetCache)?\nЗанятые файлы будут пропущены."),
                L.T("Очистить")))
        {
            return;
        }

        await RunExclusiveAsync("очистка Temp", async ct =>
        {
            StatusText = L.T("Очистка временных файлов…");
            IsMeasuringTemp = true;
            var progress = new Progress<string>(line => StatusText = L.S(line));
            var result = await _cleanupService
                .CleanAsync(FileCleanupService.GetTempTargets(), progress, ct)
                .ConfigureAwait(true);
            StatusText = FormatCleanupResult(result);
            if (result.IsSuccess && result.Value is { Count: > 0 })
            {
                // BytesFreed — реально измеренные байты по целям, которые существовали.
                var bytesFreed = result.Value.Where(item => item.Existed).Sum(item => item.BytesDeleted);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Очистка"),
                    L.T("Временные файлы"),
                    HistoryEvent.StatusOk,
                    FormatCleanupResult(result),
                    bytesFreed));
            }

            RefreshTempSize();
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanBrowsersAsync()
    {
        if (!Confirm(L.T("Очистка кэша браузеров"),
                L.T("Удалить кэш браузеров (Chrome, Edge, Яндекс, Brave, Vivaldi, Firefox, Opera)?\nЗакладки и пароли не затрагиваются. Закройте браузеры, иначе часть кэша будет занята."),
                L.T("Очистить")))
        {
            return;
        }

        await RunExclusiveAsync("очистка браузеров", async ct =>
        {
            StatusText = L.T("Очистка кэша браузеров…");
            IsMeasuringBrowsers = true;
            var progress = new Progress<string>(line => StatusText = L.S(line));
            var result = await _cleanupService
                .CleanAsync(FileCleanupService.GetBrowserCacheTargets(), progress, ct)
                .ConfigureAwait(true);
            StatusText = FormatCleanupResult(result);
            if (result.IsSuccess && result.Value is { Count: > 0 })
            {
                // BytesFreed — реально измеренные байты по целям, которые существовали.
                var bytesFreed = result.Value.Where(item => item.Existed).Sum(item => item.BytesDeleted);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Очистка"),
                    L.T("Кэш браузеров"),
                    HistoryEvent.StatusOk,
                    FormatCleanupResult(result),
                    bytesFreed));
            }

            RefreshBrowsersSize();
        }).ConfigureAwait(true);
    }

    // Полный аналог :CleanWinUpdate: ServicesBackup -> остановка служб -> очистка -> ServicesRestore.
    // DISM StartComponentCleanup отложен до итерации 5 (Maintenance).
    [RelayCommand(CanExecute = nameof(CanCleanUpdate))]
    private async Task CleanUpdateCacheAsync()
    {
        if (!Confirm(L.T("Очистка кэша обновлений Windows"),
                L.T("Будут остановлены службы обновления (wuauserv, bits, cryptsvc, msiserver) с сохранением состояния,\nзатем очищен кэш SoftwareDistribution\\Download и Delivery Optimization.\nСостояние служб будет восстановлено автоматически."),
                L.T("Очистить")))
        {
            return;
        }

        await RunExclusiveAsync("очистка кэша обновлений", async ct =>
        {
            var backupFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCU", "backup", "services", "wu_backup.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);

            StatusText = L.T("Сохранение состояния служб обновления…");
            IsMeasuringUpdateCache = true;
            var backup = await _runner.RunAsync(
                "ServicesBackup",
                new Dictionary<string, string?> { ["OutFile"] = backupFile, ["Services"] = UpdateServicesCsv },
                null,
                ct).ConfigureAwait(true);
            if (!backup.IsSuccess || !File.Exists(backupFile))
            {
                StatusText = L.T("Очистка отменена: не удалось сохранить состояние служб (код {0}).", backup.Code);
                _logger.Error($"CLEAN | WU | backup rc={backup.Code} | {backup.Message}");
                return;
            }

            StatusText = L.T("Остановка служб обновления…");

            // После успешного бэкапа восстановление служб обязано выполниться при любом
            // исходе — отмене, ошибке или исключении. Поэтому очистка идёт в try,
            // а ServicesRestore — в finally, с токеном без отмены.
            var cleaned = L.T("Очистка не выполнена.");
            var cancelled = false;
            try
            {
                foreach (var serviceName in UpdateServicesCsv.Split(','))
                {
                    var stopResult = await _serviceManager.StopAsync(serviceName, ct).ConfigureAwait(true);
                    _logger.Info("CLEAN | WU | " + stopResult.Message);
                }

                var progress = new Progress<string>(line => StatusText = L.S(line));
                var cleanResult = await _cleanupService
                    .CleanAsync(FileCleanupService.GetUpdateCacheTargets(), progress, ct)
                    .ConfigureAwait(true);
                cleaned = FormatCleanupResult(cleanResult);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                StatusText = L.T("Восстановление исходного состояния служб…");
                var restore = await _runner.RunAsync(
                    "ServicesRestore",
                    new Dictionary<string, string?> { ["InFile"] = backupFile },
                    null,
                    CancellationToken.None).ConfigureAwait(true);

                if (!restore.IsSuccess)
                {
                    StatusText = cleaned + " " + L.T("Часть служб не удалось вернуть — см. лог.");
                    _logger.Warn($"CLEAN | WU | restore rc={restore.Code} | {restore.Message}");
                }
                else
                {
                    // Финальный статус выставляется и при отмене: иначе статус навсегда
                    // остаётся «Восстановление исходного состояния служб…».
                    StatusText = cancelled
                        ? L.T("Операция отменена. Состояние служб восстановлено.")
                        : cleaned + " " + L.T("Состояние служб восстановлено.");
                    _logger.Info("CLEAN | WU | restore ok");
                    if (!cancelled)
                    {
                        _history.Enqueue(new HistoryEvent(
                            DateTime.Now,
                            L.T("Очистка"),
                            L.T("Кэш обновлений"),
                            HistoryEvent.StatusOk,
                            cleaned));
                    }
                }
            }

            RefreshUpdateCacheSize();
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | cleanup operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        _operationCts?.Cancel();
    }

    private bool CanClean() => !IsBusy && IsAdmin;
    private bool CanCleanUpdate() => !IsBusy && IsAdmin && IsSCUAvailable;
    private bool CanCancel() => IsBusy;

    // ===================== Измерение объёмов =====================

    // Счётчик запросов: результат устаревшего подсчёта (например, после быстрого
    // повторного открытия вкладки) не должен перезаписать свежий.
    private int _measureVersion;

    // Кнопка «Обновить»: ручной пересчёт объёмов карточек — те же четыре независимых подсчёта,
    // что и при открытии вкладки. Подсчёт не требует прав администратора,
    // блокируется только на время операции очистки.
    [RelayCommand(CanExecute = nameof(CanRefreshSizes))]
    private async Task RefreshAsync()
    {
        StatusText = L.T("Пересчёт объёмов…");
        await ActivateAsync().ConfigureAwait(true);
        StatusText = L.T("Объёмы пересчитаны.");
    }

    private bool CanRefreshSizes() => !IsBusy;

    // Пересчёт объёмов при открытии вкладки: четыре независимых подсчёта — каждая
    // карточка показывает индикатор ровно до готовности своих данных.
    public async Task ActivateAsync()
    {
        var version = ++_measureVersion;
        RecycleBinSizeText = DownloadsSizeText = TempSizeText = BrowsersSizeText = UpdateCacheSizeText = string.Empty;
        IsMeasuringRecycleBin = IsMeasuringDownloads = IsMeasuringTemp = IsMeasuringBrowsers = IsMeasuringUpdateCache = true;

        try
        {
            // Один сбойный замер не должен обнулять результаты остальных
            // (аудит): каждая задача ловит своё исключение и возвращает null —
            // карточка покажет «не удалось измерить».
            var recycleBin = Task.Run(async () =>
            {
                try
                {
                    return Interop.RecycleBinInfo.Query();
                }
                catch (Exception exception)
                {
                    _logger.Warn("CLEAN | recycle bin measure failed | " + exception.Message);
                    return null;
                }
            });
            var downloads = Task.Run(() => MeasureSafe(() => FileCleanupService.MeasureBytes(FileCleanupService.GetDownloadsTargets())));
            var temp = Task.Run(() => MeasureSafe(() => FileCleanupService.MeasureBytes(FileCleanupService.GetTempTargets())));
            var browsers = Task.Run(() => MeasureSafe(() => FileCleanupService.MeasureBytes(FileCleanupService.GetBrowserCacheTargets())));
            var updates = Task.Run(() => MeasureSafe(() => FileCleanupService.MeasureBytes(FileCleanupService.GetUpdateCacheTargets())));

            await Task.WhenAll(recycleBin, downloads, temp, browsers, updates).ConfigureAwait(true);
            if (version != _measureVersion)
            {
                return;
            }

            RecycleBinSizeText = recycleBin.Result is { } bin ? FormatSize(bin.Bytes) : L.T("не удалось измерить");
            DownloadsSizeText = downloads.Result is { } downloadsBytes ? FormatSize(downloadsBytes) : L.T("не удалось измерить");
            TempSizeText = temp.Result is { } tempBytes ? FormatSize(tempBytes) : L.T("не удалось измерить");
            BrowsersSizeText = browsers.Result is { } browsersBytes ? FormatSize(browsersBytes) : L.T("не удалось измерить");
            UpdateCacheSizeText = updates.Result is { } updatesBytes ? FormatSize(updatesBytes) : L.T("не удалось измерить");
        }
        catch (Exception exception)
        {
            _logger.Warn("CLEAN | size measure failed | " + exception.Message);
        }
        finally
        {
            // Сброс только актуальному замеру (аудит): при быстром повторном
            // открытии вкладки первый finally не гасил бы спиннеры второго —
            // иначе индикаторы исчезали до готовности данных.
            if (version == _measureVersion)
            {
                // Сброс безусловно: если во время подсчёта стартовала очистка (версия сменилась,
                // тексты не записаны), остающийся спиннер иначе висел бы вечно. Спиннер самой
                // очистки выставляется заново своим Refresh-потоком.
                IsMeasuringRecycleBin = IsMeasuringDownloads = IsMeasuringTemp = IsMeasuringBrowsers = IsMeasuringUpdateCache = false;
            }
        }
    }

    private long? MeasureSafe(Func<long> measure)
    {
        try
        {
            return measure();
        }
        catch (Exception exception)
        {
            _logger.Warn("CLEAN | size measure failed | " + exception.Message);
            return null;
        }
    }

    // Пересчёт одной категории после её очистки (спиннер на время подсчёта).
    private async Task RefreshSizeAsync(Func<long> measure, Action<string> setText, Action<bool> setMeasuring, int version)
    {
        setMeasuring(true);
        try
        {
            var bytes = await Task.Run(measure).ConfigureAwait(true);
            if (version == _measureVersion)
            {
                setText(FormatSize(bytes));
            }
        }
        catch (Exception exception)
        {
            _logger.Warn("CLEAN | size refresh failed | " + exception.Message);
        }
        finally
        {
            setMeasuring(false);
        }
    }

    private void RefreshRecycleBinSize() => _ = RefreshSizeAsync(
        () => Interop.RecycleBinInfo.Query()?.Bytes ?? 0,
        value => RecycleBinSizeText = value,
        value => IsMeasuringRecycleBin = value,
        Interlocked.Increment(ref _measureVersion));

    private void RefreshTempSize() => _ = RefreshSizeAsync(
        () => FileCleanupService.MeasureBytes(FileCleanupService.GetTempTargets()),
        value => TempSizeText = value,
        value => IsMeasuringTemp = value,
        Interlocked.Increment(ref _measureVersion));

    private void RefreshDownloadsSize() => _ = RefreshSizeAsync(
        () => FileCleanupService.MeasureBytes(FileCleanupService.GetDownloadsTargets()),
        value => DownloadsSizeText = value,
        value => IsMeasuringDownloads = value,
        Interlocked.Increment(ref _measureVersion));

    private void RefreshBrowsersSize() => _ = RefreshSizeAsync(
        () => FileCleanupService.MeasureBytes(FileCleanupService.GetBrowserCacheTargets()),
        value => BrowsersSizeText = value,
        value => IsMeasuringBrowsers = value,
        Interlocked.Increment(ref _measureVersion));

    private void RefreshUpdateCacheSize() => _ = RefreshSizeAsync(
        () => FileCleanupService.MeasureBytes(FileCleanupService.GetUpdateCacheTargets()),
        value => UpdateCacheSizeText = value,
        value => IsMeasuringUpdateCache = value,
        Interlocked.Increment(ref _measureVersion));

    // Единый форматтер размеров (аудит UX): раньше здесь были две частные
    // реализации с разными единицами и культурами — теперь общий HumanSize.
    internal static string FormatSize(long bytes) => HumanSize.Format(bytes);

    private bool Confirm(string title, string message, string confirmText) =>
        _dialogs.Ask(title, message, confirmText);

    // Форматтер строит локализованный текст сразу (L.T по шаблону с параметрами):
    // прежний путь — готовые русские строки, которые L.S не находит в словаре целиком.
    internal static string FormatCleanupResult(Result<IReadOnlyList<CleanupItemResult>> result)
    {
        if (!result.IsSuccess)
        {
            return L.T("Очистка не выполнена (код {0}): {1}", result.Code, result.Message);
        }

        if (result.Value is null || result.Value.Count == 0)
        {
            return L.T("Нечего очищать: целевые папки не найдены.");
        }

        var deletedFiles = 0L;
        var deletedBytes = 0L;
        var remaining = 0L;
        foreach (var item in result.Value)
        {
            deletedFiles += item.FilesDeleted;
            deletedBytes += item.BytesDeleted;
            remaining += item.FilesBefore - item.FilesDeleted;
        }

        var summary = L.T("Удалено файлов: {0} ({1}).", deletedFiles, FormatBytes(deletedBytes));
        if (remaining > 0)
        {
            summary += L.T(" Осталось занятых: {0}.", remaining);
        }

        return summary;
    }

    internal static string FormatBytes(long bytes) => HumanSize.Format(bytes);

    private Task RunExclusiveAsync(string title, Func<CancellationToken, Task> action) =>
        SectionOperations.RunExclusiveAsync(title, "CLEAN", () => IsBusy, value => IsBusy = value, () => _operationCts, value => _operationCts = value, value => StatusText = value, _logger, action);}
