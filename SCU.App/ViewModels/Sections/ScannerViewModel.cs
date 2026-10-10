using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Models.Scan;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Раздел 21 «Сканер» (документ п. 27): on-demand сканирование выбранного
// файла/папки (кнопка или drag-and-drop) через ScannerCore.exe, карантин и
// безопасный просмотр содержимого.
// Никакого постоянного фонового сканера — только ручной запуск и отмена.
public partial class ScannerViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly ScannerRunner _scanner;
    private readonly HistoryStore _history;
    private readonly QuarantineService _quarantine;
    private readonly IConfirmDialogService _dialogs;
    private readonly IFilePickerService _filePicker;
    private readonly IShellOpenService _shell;
    private readonly ScannerUpdateService _updateService = new();
    private readonly ScannerDbStateStore _dbStateStore;
    private CancellationTokenSource? _operationCts;

    // Сериализация доступа к ScannerCore: автообновление базы идёт вне IsBusy
    // (тихое), и без общего gate оно могло стартовать параллельно с ручным
    // сканом/установкой пакета — два процесса ScannerCore и гонка за файл базы.
    // internal: тест-шов — регрессия badge отмены занимает gate извне.
    internal readonly SemaphoreSlim _scannerGate = new(1, 1);

    // Останавливает автообновление при закрытии приложения.
    private readonly CancellationTokenSource _lifetimeCts = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallDatabasePackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateDatabaseOnlineCommand))]
    [NotifyCanExecuteChangedFor(nameof(ViewFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ViewFolderCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _lastScanSummaryText = string.Empty;

    // Бейдж исхода скана (аудит п. 15.2): «не найдено» ≠ «не найдено, но
    // проверено не всё». Текст и ключ вида бейджа ставятся вместе со статусом.
    [ObservableProperty]
    private string _outcomeBadge = string.Empty;

    [ObservableProperty]
    private string _outcomeBadgeKind = "None"; // Clean | Threats | Partial | Failed | Cancelled | None

    private void SetOutcomeBadge(ScanOutcome outcome, long detections = 0)
    {
        OutcomeBadgeKind = outcome switch
        {
            ScanOutcome.Clean => "Clean",
            ScanOutcome.Threats => "Threats",
            ScanOutcome.Partial => "Partial",
            ScanOutcome.Cancelled => "Cancelled",
            ScanOutcome.Failed => "Failed",
            _ => "None",
        };
        OutcomeBadge = outcome switch
        {
            ScanOutcome.Clean => L.T("Проверено полностью"),
            ScanOutcome.Threats => L.T("Обнаружено проблем: {0}", detections),
            ScanOutcome.Partial => L.T("Проверено частично"),
            ScanOutcome.Cancelled => L.T("Прервано"),
            ScanOutcome.Failed => L.T("Ошибка проверки"),
            _ => string.Empty,
        };
    }

    // Database freshness (п. 32): версия и дата базы из события started.
    [ObservableProperty]
    private string _databaseInfoText = L.T("База: встроенная (EICAR)");

    // Прогресс сканирования: процент по файлам и объёму + оценка времени до конца.
    [ObservableProperty]
    private double _scanProgressPercent;

    [ObservableProperty]
    private string _scanEtaText = string.Empty;

    private readonly Dictionary<string, long> _scanFileSizes = new(StringComparer.OrdinalIgnoreCase);
    private long _scanTotalFiles;
    private long _scanTotalBytes;
    private long _scanBytesDone;
    private string? _scanCurrentFile;
    private DateTime _scanStartedUtc;
    private bool _scanElapsedKnown;

    public ScannerViewModel(Logger logger, ScannerRunner scanner, HistoryStore history)
        : this(logger, scanner, history, new ConfirmDialogService(), new FilePickerService(), new ShellOpenService())
    {
    }

    public ScannerViewModel(
        Logger logger,
        ScannerRunner scanner,
        HistoryStore history,
        IConfirmDialogService dialogs,
        IFilePickerService filePicker,
        IShellOpenService shell)
    {
        _logger = logger;
        _scanner = scanner;
        _history = history;
        _quarantine = new QuarantineService();
        _dialogs = dialogs;
        _filePicker = filePicker;
        _shell = shell;
        _dbStateStore = new ScannerDbStateStore(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU", "scanner-db-state.json"));
        // Статус пустой: строка «ScannerCore доступен…» дублировала очевидное
        // (раздел открыт — кнопки видны). Ошибка («ScannerCore не найден»)
        // остаётся: без неё недоступность сканера выглядела бы как сбой кнопок.
        StatusText = scanner.IsAvailable
            ? string.Empty
            : L.T("ScannerCore.exe не найден — сканирование недоступно.");
    }

    public bool ScannerAvailable => _scanner.IsAvailable;

    public bool IsInteractive => !IsBusy && ScannerAvailable;

    public ObservableCollection<DetectionDto> Detections { get; } = new();

    public ObservableCollection<QuarantineItem> QuarantineItems { get; } = new();

    private bool CanScan() => !IsBusy && ScannerAvailable;
    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanFileAsync()
    {
        var path = _filePicker.PickOpenFile(L.T("Выбор файла для проверки"), filter: null);
        if (path is null)
        {
            return;
        }

        await RunScanAsync(path, scanDirectory: false).ConfigureAwait(true);
    }

    // Пути из drag-and-drop (View передаёт готовый список из DataObject).
    // Первый элемент — файл или существующая директория; остальное игнорируем:
    // у среза один target на сканирование.
    public async Task HandleDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var target = paths[0];
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        // Проверяется один путь за раз: остальные — явно, а не молча.
        if (paths.Count > 1)
        {
            StatusText = L.T("За раз проверяется один объект — обработан только первый.");
        }

        if (Directory.Exists(target))
        {
            await RunScanAsync(target, scanDirectory: true).ConfigureAwait(true);
            return;
        }

        if (File.Exists(target))
        {
            await RunScanAsync(target, scanDirectory: false).ConfigureAwait(true);
            return;
        }

        StatusText = L.T("Путь не найден: {0}", target);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | antivirus scan");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена сканирования…");
    }

    // ===================== Карантин (документ п. 21/46) =====================

    [RelayCommand]
    private void Quarantine(DetectionDto detection)
    {
        if (detection is null)
        {
            return;
        }

        var target = detection.QuarantineTarget;
        if (!_dialogs.Ask(
                L.T("Карантин"),
                L.T("Изолировать {0}?\nФайл будет перемещён в карантин, оригинал удалён с диска.", target),
                L.T("Изолировать")))
        {
            return;
        }

        if (_quarantine.Quarantine(detection, out var error))
        {
            Detections.Remove(detection);
            StatusText = L.T("Файл изолирован: {0}", target);
            _logger.Info("QUARANTINE | added | " + target);
            _history.Enqueue(new HistoryEvent(
                DateTime.Now,
                L.T("Сканер"),
                L.T("Изоляция в карантин"),
                HistoryEvent.StatusOk,
                Path.GetFileName(target)));
            RefreshQuarantineList();
        }
        else
        {
            StatusText = L.T("Не удалось изолировать: {0}", error);
            _logger.Error("QUARANTINE | add failed | " + target + " | " + error);
        }
    }

    [RelayCommand]
    private void RestoreQuarantined(QuarantineItem item)
    {
        if (item is null)
        {
            return;
        }

        if (!_dialogs.Ask(
                L.T("Восстановление из карантина"),
                L.T("Вернуть {0} на прежнее место?\nФайл снова станет доступен для запуска.", item.OriginalPath),
                L.T("Восстановить")))
        {
            return;
        }

        if (_quarantine.Restore(item, out var error))
        {
            QuarantineItems.Remove(item);
            StatusText = L.T("Восстановлено: {0}", item.OriginalPath);
            _logger.Info("QUARANTINE | restored | " + item.OriginalPath);
            _history.Enqueue(new HistoryEvent(
                DateTime.Now,
                L.T("Сканер"),
                L.T("Восстановление из карантина"),
                HistoryEvent.StatusOk,
                Path.GetFileName(item.OriginalPath)));
        }
        else
        {
            StatusText = L.T("Не удалось восстановить: {0}", error);
            _logger.Error("QUARANTINE | restore failed | " + item.OriginalPath + " | " + error);
        }
    }

    [RelayCommand]
    private void DeleteQuarantined(QuarantineItem item)
    {
        if (item is null)
        {
            return;
        }

        if (!_dialogs.Ask(
                L.T("Удаление навсегда"),
                L.T("Безвозвратно удалить {0} из карантина?\nДействие нельзя отменить.", item.OriginalPath),
                L.T("Удалить навсегда")))
        {
            return;
        }

        if (_quarantine.DeletePermanently(item, out var error))
        {
            QuarantineItems.Remove(item);
            StatusText = L.T("Удалено навсегда: {0}", item.OriginalPath);
            _logger.Warn("QUARANTINE | deleted | " + item.OriginalPath);
            _history.Enqueue(new HistoryEvent(
                DateTime.Now,
                L.T("Сканер"),
                L.T("Удаление из карантина"),
                HistoryEvent.StatusOk,
                Path.GetFileName(item.OriginalPath)));
        }
        else
        {
            StatusText = L.T("Не удалось удалить: {0}", error);
            _logger.Error("QUARANTINE | delete failed | " + item.OriginalPath + " | " + error);
        }
    }

    // ===================== Безопасный просмотр (п. 22/27) =====================

    [RelayCommand(CanExecute = nameof(CanView))]
    private void ViewFile()
    {
        var path = _filePicker.PickOpenFile(L.T("Просмотр файла в безопасной среде"), filter: null);
        if (path is not null)
        {
            SafeViewerViewModel.ShowFile(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanView))]
    private void ViewFolder()
    {
        var folder = _filePicker.PickOpenFolder(L.T("Просмотр папки в безопасной среде"));
        if (folder is not null)
        {
            SafeViewerViewModel.ShowFolder(folder);
        }
    }

    // Член архива нельзя открыть как файл — открываем контейнер с пометкой.
    [RelayCommand]
    private void ViewDetection(DetectionDto detection)
    {
        if (detection is null)
        {
            return;
        }

        var verdictLine = L.T("Вердикт скана: {0} ({1})", detection.Verdict, detection.RuleId);
        if (detection.IsVirtual)
        {
            SafeViewerViewModel.ShowFile(
                detection.QuarantineTarget,
                verdictLine,
                L.T("Обнаружение в члене архива: {0}. Открыт контейнер.", detection.Path));
            return;
        }

        // Persistence-запись: Path — псевдопуть (HKCU\...\Run\X), смотреть
        // можно только реальный файл цели. Кнопка скрыта, когда цели нет,
        // но ShowFile и сам деградирует в «Файл не найден» (fail-safe).
        SafeViewerViewModel.ShowFile(detection.QuarantineTarget, verdictLine);
    }

    [RelayCommand]
    private void ViewQuarantined(QuarantineItem item)
    {
        if (item is null)
        {
            return;
        }

        var objectPath = _quarantine.GetObjectPath(item.Id);
        if (!File.Exists(objectPath))
        {
            StatusText = L.T("Объект карантина не найден: {0}", item.Id);
            return;
        }

        SafeViewerViewModel.ShowFile(
            objectPath,
            L.T("Вердикт скана: {0}", item.Verdict),
            L.T("Изолированный объект; оригинал: {0}", item.OriginalPath));
    }

    private bool CanView() => !IsBusy;

    // Обход каталога карантина — вне UI-потока (аудит производительности):
    // раньше список перечитывался синхронно при каждом открытии секции 21.
    public void RefreshQuarantineList()
    {
        TaskRunner.RunAndForget(RefreshQuarantineListAsync(), _logger, "quarantine refresh");
    }

    private async Task RefreshQuarantineListAsync()
    {
        var items = await Task.Run(() => _quarantine.List().ToList()).ConfigureAwait(false);
        var corrupted = _quarantine.CorruptedMetadataCount;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            QuarantineItems.Clear();
            foreach (var item in items)
            {
                QuarantineItems.Add(item);
            }

            // П. 29 аудита 2: скрытые из-за повреждённых метаданных записи не
            // превращаются в молчаливое «карантин пуст».
            if (corrupted > 0)
            {
                AppNotificationCenter.Instance.Push(
                    L.T("Карантин: часть записей недоступна"),
                    L.T("Некоторые записи карантина повреждены и не отображаются: {0}.", corrupted),
                    AppNotificationKind.Warn);
            }
        });
    }

    // Автообновление базы при старте приложения: тихое — состояния вкладки и
    // историю не трогает; о новом пакете сообщает уведомлением. Сбой (нет сети,
    // сервер недоступен) остаётся только в журнале.
    public async Task AutoUpdateDatabaseAsync()
    {
        if (!IsScannerReadyForCommand())
        {
            return;
        }

        var tempPath = string.Empty;
        try
        {
            // Общий gate со сканом и ручными обновлениями: установка базы не должна
            // выполняться параллельно со сканом. Отмена — при закрытии приложения.
            await _scannerGate.WaitAsync(_lifetimeCts.Token).ConfigureAwait(true);
            try
            {
                // Сверка версии до скачивания (пакет — десятки МБ): если на релизе
                // та же версия, что установлена, пакет не перекачивается. Узнать
                // версию не удалось — качаем, как раньше (fail-open).
                var remote = await _updateService
                    .GetLatestDbVersionAsync(_lifetimeCts.Token)
                    .ConfigureAwait(true);
                if (remote.IsSuccess && remote.Value is { } remoteVersion)
                {
                    var installed = _dbStateStore.Load();
                    if (installed == remoteVersion)
                    {
                        _logger.Info("DBUPDATE | auto skipped (already installed) | " + remoteVersion);
                        DatabaseInfoText = L.T("База: {0}", L.Date(remoteVersion));
                        return;
                    }
                }

                var download = await _updateService
                    .DownloadPackageAsync(_updateService.ResolveUrl(), _lifetimeCts.Token)
                    .ConfigureAwait(true);
                if (!download.IsSuccess || download.Value is null)
                {
                    _logger.Warn($"DBUPDATE | auto download failed | rc={download.Code} | {download.Message}");
                    return;
                }

                tempPath = download.Value;
                var result = await _scanner.RunUpdateAsync(tempPath, _lifetimeCts.Token).ConfigureAwait(true);
                if (result.IsSuccess && result.Value is not null)
                {
                    // Версия базы — календарная (yyyy.MM.dd), в карточке уведомления и
                    // строке «База» показываем её в едином виде дд.мм.гггг.
                    var version = L.Date(ExtractVersion(result.Value));
                    DatabaseInfoText = L.T("База: {0}", version);
                    _dbStateStore.Save(ExtractVersion(result.Value));
                    _logger.Info("DBUPDATE | auto | " + result.Value);
                    AppNotificationCenter.Instance.Push(
                        L.T("База сканера обновлена"),
                        L.T("Установлен пакет базы: {0}", version),
                        AppNotificationKind.Success);
                }
                else
                {
                    _logger.Warn($"DBUPDATE | auto failed | rc={result.Code} | {result.Message}");
                }
            }
            finally
            {
                _scannerGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("DBUPDATE | auto cancelled (app closing)");
        }
        catch (Exception exception)
        {
            _logger.Warn("DBUPDATE | auto failed | " + exception.Message);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Временный файл не критичен.
        }
    }

    // Готовность сканера для других разделов (авто-скан загрузок браузера).
    public bool IsScannerReady => _scanner.IsAvailable && ScannerAvailable;

    // Проверка одного файла из других разделов под ТЕМ ЖЕ общим gate, что и
    // ручной скан и обновления базы: два ScannerCore параллельно недопустимы —
    // они делят файл базы и disk cache.
    public async Task<Result<ScanResultDto>> RunFileScanAsync(string filePath, CancellationToken ct = default)
    {
        await _scannerGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            return await _scanner
                .RunScanAsync(filePath, scanDirectory: false, ct: ct)
                .ConfigureAwait(true);
        }
        finally
        {
            _scannerGate.Release();
        }
    }

    private bool IsScannerReadyForCommand() =>
        _scanner.IsAvailable && ScannerAvailable;

    // Обновление базы по HTTPS (п. 30): скачать во временный файл →
    // установка с проверкой подписи в ScannerCore.
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task UpdateDatabaseOnlineAsync()
    {
        // Единый lifecycle с ручным сканом (аудит HIGH): операция получает
        // собственный CTS, связанный с lifetime — CancelOngoing()/Dispose
        // отменяют загрузку и установку пакета.
        if (IsBusy)
        {
            return;
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token, _operationCts.Token);
        var ct = linkedCts.Token;

        IsBusy = true;
        StatusText = L.T("Загрузка пакета базы…");
        var tempPath = string.Empty;
        var gateHeld = false;
        try
        {
            // Gate с тихим автообновлением базы и сканом.
            await _scannerGate.WaitAsync(ct).ConfigureAwait(true);
            gateHeld = true;

            var download = await _updateService.DownloadPackageAsync(_updateService.ResolveUrl(), ct)
                .ConfigureAwait(true);
            if (!download.IsSuccess || download.Value is null)
            {
                StatusText = L.T("Ошибка: {0}", download.Message);
                _logger.Error($"DBUPDATE | download failed | rc={download.Code} | {download.Message}");
                return;
            }

            tempPath = download.Value;
            StatusText = L.T("Установка загруженного пакета…");
            var result = await _scanner.RunUpdateAsync(tempPath, ct).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is not null)
            {
                // Версия базы — календарная (yyyy.MM.dd): в статусе показываем дд.мм.гггг.
                StatusText = L.T("База обновлена: {0}", L.Date(ExtractVersion(result.Value)));
                DatabaseInfoText = L.T("База: {0}", L.Date(ExtractVersion(result.Value)));
                _dbStateStore.Save(ExtractVersion(result.Value));
                _logger.Info("DBUPDATE | online | " + result.Value);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сканер"),
                    L.T("Обновление базы (интернет)"),
                    HistoryEvent.StatusOk,
                    _updateService.ResolveUrl()));
            }
            else
            {
                StatusText = L.T("Ошибка: {0}", result.Message);
                _logger.Error($"DBUPDATE | online failed | rc={result.Code} | {result.Message}");
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сканер"),
                    L.T("Обновление базы (интернет)"),
                    HistoryEvent.StatusFail,
                    _updateService.ResolveUrl()));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = L.T("Отменено.");
            _logger.Warn("DBUPDATE | cancelled");
        }
        catch (Exception exception)
        {
            StatusText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("DBUPDATE | " + exception);
        }
        finally
        {
            // Временный пакет удаляется независимо от исхода установки.
            if (tempPath.Length > 0)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
            }

            if (gateHeld)
            {
                _scannerGate.Release();
            }

            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task InstallDatabasePackageAsync()
    {
        var path = _filePicker.PickOpenFile(
            L.T("Установка пакета базы (offline)"), "Пакет базы сигнатур (*.zip)|*.zip");
        if (path is null)
        {
            return;
        }

        // Единый lifecycle с ручным сканом (аудит HIGH): offline-установка тоже
        // отменяема — собственный CTS, связанный с lifetime.
        if (IsBusy)
        {
            return;
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token, _operationCts.Token);
        var ct = linkedCts.Token;

        IsBusy = true;
        StatusText = L.T("Установка пакета базы…");
        var gateHeld = false;
        try
        {
            // Gate с тихим автообновлением базы и сканом.
            await _scannerGate.WaitAsync(ct).ConfigureAwait(true);
            gateHeld = true;

            var result = await _scanner.RunUpdateAsync(path, ct).ConfigureAwait(true);
            if (result.IsSuccess && result.Value is not null)
            {
                StatusText = result.Value;
                DatabaseInfoText = L.T("База: {0}", L.Date(ExtractVersion(result.Value)));
                _logger.Info("DBUPDATE | " + result.Value);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сканер"),
                    L.T("Установка пакета базы"),
                    HistoryEvent.StatusOk,
                    Path.GetFileName(path)));
            }
            else
            {
                // Пакет с плохой подписью отклонён — база не изменилась (п. 33).
                StatusText = L.T("Ошибка: {0}", result.Message);
                _logger.Error($"DBUPDATE | failed | rc={result.Code} | {result.Message}");
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сканер"),
                    L.T("Установка пакета базы"),
                    HistoryEvent.StatusFail,
                    Path.GetFileName(path)));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = L.T("Отменено.");
            _logger.Warn("DBUPDATE | install cancelled");
        }
        finally
        {
            if (gateHeld)
            {
                _scannerGate.Release();
            }

            IsBusy = false;
            ScanEtaText = string.Empty;
            _scanFileSizes.Clear();
        }
    }

    // Подсчёт целевого объекта: файл → сам файл; папка → все файлы рекурсивно.
    private void EnumerateScanTarget(string path, CancellationToken ct)
    {
        try
        {
            if (File.Exists(path))
            {
                _scanFileSizes[path] = new FileInfo(path).Length;
            }
            else if (Directory.Exists(path))
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    // C++-энумератор сканера обрабатывает hidden/system файлы —
                    // знаменатель прогресса обязан их считать, иначе процент
                    // достигает 100% задолго до фактического конца скана.
                    AttributesToSkip = 0,
                };
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
                {
                    // Отмена обязана прерывать сам обход, а не только ожидание
                    // его запуска: на большой папке Task.Run-токен до запуска
                    // делегата ничего не отменял.
                    ct.ThrowIfCancellationRequested();
                    _scanFileSizes[file.FullName] = file.Length;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Подсчёт не удался — ETA по файлам, проценты продолжат считаться.
            _logger.Warn("SCAN | size estimate failed | " + exception.Message);
        }

        _scanTotalFiles = _scanFileSizes.Count;
        _scanTotalBytes = _scanFileSizes.Values.Sum();
    }

    // Процент: гибрид файлы+объём. Объём считается по файлам, которые сканер уже
    // reported как текущие (события progress идут в порядке обработки) — точнее
    // файлов, потому что большие файлы сканируются дольше.
    private void UpdateScanProgress(ScanEvent scanEvent)
    {
        if (!_scanElapsedKnown)
        {
            _scanStartedUtc = DateTime.UtcNow;
            _scanElapsedKnown = true;
        }

        if (_scanCurrentFile is { } previous
            && !string.Equals(previous, scanEvent.Current, StringComparison.OrdinalIgnoreCase)
            && _scanFileSizes.TryGetValue(previous, out var size))
        {
            _scanBytesDone += size;
        }
        _scanCurrentFile = scanEvent.Current;

        var filesDone = scanEvent.Scanned + scanEvent.Skipped;
        double fraction;
        if (_scanTotalBytes > 0 && _scanBytesDone > 0)
        {
            var bytesFraction = Math.Clamp((double)_scanBytesDone / _scanTotalBytes, 0.0, 1.0);
            var filesFraction = _scanTotalFiles > 0
                ? Math.Clamp((double)filesDone / _scanTotalFiles, 0.0, 1.0)
                : bytesFraction;
            // Объём доминирует, файлы подтягивают заниженный bytesDone (события
            // progress идут с шагом больше одного файла).
            fraction = Math.Max(bytesFraction, 0.35 * bytesFraction + 0.65 * filesFraction);
        }
        else if (_scanTotalFiles > 0)
        {
            fraction = Math.Clamp((double)filesDone / _scanTotalFiles, 0.0, 1.0);
        }
        else
        {
            ScanProgressPercent = 0;
            ScanEtaText = string.Empty;
            return;
        }

        ScanProgressPercent = Math.Round(fraction * 100.0, 1);

        var elapsed = DateTime.UtcNow - _scanStartedUtc;
        ScanEtaText = fraction < 0.005
            ? L.T("Осталось ≈ оценка…")
            : L.T("Осталось ≈ {0}", FormatEta(TimeSpan.FromSeconds(elapsed.TotalSeconds * (1.0 - fraction) / fraction)));
    }

    private static string FormatEta(TimeSpan remaining)
    {
        if (remaining.TotalMinutes >= 1)
        {
            return $"{(int)remaining.TotalMinutes} мин {remaining.Seconds} с";
        }

        return Math.Ceiling(remaining.TotalSeconds) + " с";
    }

    private static string ExtractVersion(string updateMessage)
    {
        // "База обновлена: <version> (записей: N)" → <version>.
        var start = updateMessage.IndexOf(':');
        var end = updateMessage.IndexOf('(', start);
        return start >= 0 && end > start
            ? updateMessage[(start + 2)..end].Trim()
            : updateMessage;
    }

    public void CancelOngoing() => _operationCts?.Cancel();

    public void Dispose()
    {
        // Gate не диспозим: держатель (например, автообновление) ещё releasing в
        // finally, Release на disposed SemaphoreSlim бросил бы исключение.
        _operationCts?.Cancel();
        _lifetimeCts.Cancel();
    }

    private async Task RunScanAsync(string path, bool scanDirectory)
    {
        if (IsBusy)
        {
            return;
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        IsBusy = true;
        Detections.Clear();
        LastScanSummaryText = string.Empty;
        OutcomeBadge = string.Empty;
        OutcomeBadgeKind = "None";
        ScanProgressPercent = 0;
        ScanEtaText = L.T("Оценка объёма…");
        StatusText = L.T("Сканирование: {0}…", path);

        var gateHeld = false;
        try
        {
            // Gate с тихим автообновлением базы: не стартуем скан, пока идёт установка пакета.
            // Ждём на linked-токене: Cancel во время ожидания gate должен
            // немедленно прервать ожидание, а не оставить «отменяемую»
            // операцию висеть до чужого релиза.
            using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts.Token, _operationCts.Token);
            await _scannerGate.WaitAsync(gateCts.Token).ConfigureAwait(true);
            gateHeld = true;

            // Предварительный подсчёт файлов и объёма: база для процента и ETA.
            _scanFileSizes.Clear();
            _scanTotalFiles = 0;
            _scanTotalBytes = 0;
            _scanBytesDone = 0;
            _scanCurrentFile = null;
            _scanElapsedKnown = false;
            await Task.Run(() => EnumerateScanTarget(path, _operationCts.Token), _operationCts.Token)
                .ConfigureAwait(true);
            var progress = new Progress<ScanEvent>(scanEvent =>
            {
                // Событие уже разобрано в ScannerRunner — VM только отражает статус.
                if (scanEvent.Kind == ScanEventKind.Progress)
                {
                    UpdateScanProgress(scanEvent);
                    StatusText = L.T("Просканировано: {0} | Пропущено: {1} | Обнаружений: {2}",
                        scanEvent.Scanned, scanEvent.Skipped, scanEvent.Detections);
                }
                else if (scanEvent.Kind == ScanEventKind.Started)
                {
                    // Database freshness (п. 32): показываем версию и дату базы.
                    // DbDate приходит от ScannerCore в ISO (yyyy-MM-dd) — приводим
                    // к единому виду интерфейса (дд.мм.гггг).
                    DatabaseInfoText = scanEvent.DbVersion.Length > 0
                        ? L.T("База: {0} (от {1})", scanEvent.DbVersion, L.Date(scanEvent.DbDate))
                        : L.T("База: встроенная (EICAR)");
                }
            });

            var result = await _scanner.RunScanAsync(path, scanDirectory, progress, _operationCts.Token)
                .ConfigureAwait(true);

            if (result.IsSuccess && result.Value is not null)
            {
                var scanResult = result.Value;
                foreach (var detection in scanResult.Detections)
                {
                    Detections.Add(detection);
                }

                var summary = scanResult.Summary;
                // Единая классификация исхода (аудит п. 14): ветвление по Outcome,
                // а не по независимому чтению Cancelled/Errors/FilesSkipped.
                SetOutcomeBadge(scanResult.Outcome, summary.Detections);
                switch (scanResult.Outcome)
                {
                    case ScanOutcome.Cancelled:
                        StatusText = L.T("Сканирование прервано. Частичный результат сохранён.");
                        break;

                    // Пропуски файлов не должны прятаться за «обнаружено» (п. SCAN-01).
                    case ScanOutcome.Partial when summary.Detections > 0:
                        StatusText = summary.FilesSkipped > 0
                            ? L.T("Обнаружено проблем: {0} (сканирование неполное, пропущено {1}).",
                                summary.Detections, summary.FilesSkipped)
                            : L.T("Обнаружено проблем: {0}.", summary.Detections);
                        AppNotificationCenter.Instance.Push(
                            L.T("Сканер: обнаружены угрозы"),
                            L.T("Проверка «{0}»: проблемных объектов — {1}.", path, summary.Detections),
                            AppNotificationKind.Danger);
                        break;

                    case ScanOutcome.Partial when summary.Errors > 0:
                        StatusText = L.T("Сканирование завершено с ошибками: часть файлов не проверена.");
                        break;

                    // П. SCAN-01/SCAN-06: skip по размеру/нечитаемости/архиву не даёт
                    // права писать «не найдено» — coverage неполный.
                    case ScanOutcome.Partial:
                        StatusText = L.T("Обнаружений не найдено, но сканирование неполное: пропущено {0}.",
                            summary.FilesSkipped);
                        break;

                    case ScanOutcome.Threats:
                        StatusText = L.T("Обнаружено проблем: {0}.", summary.Detections);
                        AppNotificationCenter.Instance.Push(
                            L.T("Сканер: обнаружены угрозы"),
                            L.T("Проверка «{0}»: проблемных объектов — {1}.", path, summary.Detections),
                            AppNotificationKind.Danger);
                        break;

                    default:
                        // «Не найдено» только при полном прохождении (документ п. 36/51):
                        // ошибки, пропуски и отмена уже обработаны выше.
                        StatusText = L.T("На момент сканирования обнаружений не найдено.");
                        break;
                }

                LastScanSummaryText = L.T("Файлов: {0} | Пропущено: {1} | Ошибок: {2}",
                    summary.FilesScanned, summary.FilesSkipped, summary.Errors);

                _logger.Info($"SCAN | finished | outcome={scanResult.Outcome} | files={summary.FilesScanned} | detections={summary.Detections} | errors={summary.Errors}");

                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сканер"),
                    L.T(scanDirectory ? "Сканирование папки" : "Проверка файла"),
                    // П. SCAN-01: неполное покрытие — не «успех без оговорок»;
                    // угрозы и сбой прогона — не «успех» вовсе.
                    scanResult.Outcome switch
                    {
                        ScanOutcome.Threats => HistoryEvent.StatusFail,
                        ScanOutcome.Partial => HistoryEvent.StatusWarn,
                        ScanOutcome.Cancelled => HistoryEvent.StatusWarn,
                        _ => HistoryEvent.StatusOk,
                    },
                    L.T("Файлов: {0}, обнаружений: {1}", summary.FilesScanned, summary.Detections)));
            }
            else
            {
                if (result.Code == -1)
                {
                    SetOutcomeBadge(ScanOutcome.Cancelled);
                }
                else
                {
                    SetOutcomeBadge(ScanOutcome.Failed);
                }

                StatusText = result.Code == -1
                    ? L.T("Сканирование отменено.")
                    : L.T("Ошибка: {0}", result.Message);
                _logger.Error($"SCAN | failed | rc={result.Code} | {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            // Единая семантика с отменой внутри ScannerCore: badge «Прервано»
            // ставится независимо от того, на каком этапе пришла отмена —
            // раньше отмена до старта ScannerCore оставляла badge пустым
            // (аудит UI-состояний).
            SetOutcomeBadge(ScanOutcome.Cancelled);
            LastScanSummaryText = L.T("Сканирование прервано пользователем.");
            StatusText = L.T("Сканирование отменено.");
        }
        catch (Exception exception)
        {
            StatusText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("SCAN | " + exception);
        }
        finally
        {
            if (gateHeld)
            {
                _scannerGate.Release();
            }

            IsBusy = false;
        }
    }
}
