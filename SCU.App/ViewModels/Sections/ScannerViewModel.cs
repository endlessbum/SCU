using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Models.Scan;
using SCU.Services;
using SCU.Services.Dashboard;
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
    private readonly ScannerUpdateService _updateService = new();
    private CancellationTokenSource? _operationCts;

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
    private string _statusText = L.T("ScannerCore: проверка доступности…");

    [ObservableProperty]
    private string _lastScanSummaryText = string.Empty;

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
    {
        _logger = logger;
        _scanner = scanner;
        _history = history;
        _quarantine = new QuarantineService();
        _dialogs = new ConfirmDialogService();
        StatusText = L.T(scanner.IsAvailable
            ? "ScannerCore доступен. Перетащите файл/папку или выберите путь."
            : "ScannerCore.exe не найден — сканирование недоступно.");
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
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Title = L.T("Выбор файла для проверки"),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunScanAsync(dialog.FileName, scanDirectory: false).ConfigureAwait(true);
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
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Title = L.T("Просмотр файла в безопасной среде"),
        };
        if (dialog.ShowDialog() == true)
        {
            SafeViewerViewModel.ShowFile(dialog.FileName);
        }
    }

    [RelayCommand(CanExecute = nameof(CanView))]
    private void ViewFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = L.T("Просмотр папки в безопасной среде"),
        };
        if (dialog.ShowDialog() == true)
        {
            SafeViewerViewModel.ShowFolder(dialog.FolderName);
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

        SafeViewerViewModel.ShowFile(detection.Path, verdictLine);
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

    public void RefreshQuarantineList()
    {
        QuarantineItems.Clear();
        foreach (var item in _quarantine.List())
        {
            QuarantineItems.Add(item);
        }
    }

    // Обновление базы по HTTPS (п. 30): скачать во временный файл →
    // установка с проверкой подписи в ScannerCore.
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task UpdateDatabaseOnlineAsync()
    {
        IsBusy = true;
        StatusText = L.T("Загрузка пакета базы…");
        var tempPath = string.Empty;
        try
        {
            var download = await _updateService.DownloadPackageAsync(_updateService.ResolveUrl())
                .ConfigureAwait(true);
            if (!download.IsSuccess || download.Value is null)
            {
                StatusText = L.T("Ошибка: {0}", download.Message);
                _logger.Error($"DBUPDATE | download failed | rc={download.Code} | {download.Message}");
                return;
            }

            tempPath = download.Value;
            StatusText = L.T("Установка загруженного пакета…");
            var result = await _scanner.RunUpdateAsync(tempPath).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                StatusText = result.Value;
                DatabaseInfoText = L.T("База: {0}", ExtractVersion(result.Value));
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

            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task InstallDatabasePackageAsync()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Пакет базы сигнатур (*.zip)|*.zip",
            Title = L.T("Установка пакета базы (offline)"),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        StatusText = L.T("Установка пакета базы…");
        try
        {
            var result = await _scanner.RunUpdateAsync(dialog.FileName).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                StatusText = result.Value;
                DatabaseInfoText = L.T("База: {0}", ExtractVersion(result.Value));
                _logger.Info("DBUPDATE | " + result.Value);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сканер"),
                    L.T("Установка пакета базы"),
                    HistoryEvent.StatusOk,
                    Path.GetFileName(dialog.FileName)));
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
                    Path.GetFileName(dialog.FileName)));
            }
        }
        finally
        {
            IsBusy = false;
            ScanEtaText = string.Empty;
            _scanFileSizes.Clear();
        }
    }

    // Подсчёт целевого объекта: файл → сам файл; папка → все файлы рекурсивно.
    private void EnumerateScanTarget(string path)
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
                };
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
                {
                    _scanFileSizes[file.FullName] = file.Length;
                }
            }
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

    public void Dispose() => _operationCts?.Cancel();

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
        ScanProgressPercent = 0;
        ScanEtaText = L.T("Оценка объёма…");
        StatusText = L.T("Сканирование: {0}…", path);

        try
        {
            // Предварительный подсчёт файлов и объёма: база для процента и ETA.
            _scanFileSizes.Clear();
            _scanTotalFiles = 0;
            _scanTotalBytes = 0;
            _scanBytesDone = 0;
            _scanCurrentFile = null;
            _scanElapsedKnown = false;
            await Task.Run(() => EnumerateScanTarget(path), _operationCts.Token).ConfigureAwait(true);
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
                    DatabaseInfoText = scanEvent.DbVersion.Length > 0
                        ? L.T("База: {0} (от {1})", scanEvent.DbVersion, scanEvent.DbDate)
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
                if (scanResult.Cancelled)
                {
                    StatusText = L.T("Сканирование прервано. Частичный результат сохранён.");
                }
                else if (summary.Errors > 0)
                {
                    StatusText = L.T("Сканирование завершено с ошибками: часть файлов не проверена.");
                }
                else if (summary.Detections > 0)
                {
                    StatusText = L.T("Обнаружено проблем: {0}.", summary.Detections);
                    AppNotificationCenter.Instance.Push(
                        L.T("Сканер: обнаружены угрозы"),
                        L.T("Проверка «{0}»: проблемных объектов — {1}.", path, summary.Detections),
                        AppNotificationKind.Danger);
                }
                else
                {
                    // «Не найдено» только при полном прохождении (документ п. 36/51):
                    // ошибки и отмена уже обработаны выше.
                    StatusText = L.T("На момент сканирования обнаружений не найдено.");
                }

                LastScanSummaryText = L.T("Файлов: {0} | Пропущено: {1} | Ошибок: {2}",
                    summary.FilesScanned, summary.FilesSkipped, summary.Errors);

                _logger.Info($"SCAN | finished | files={summary.FilesScanned} | detections={summary.Detections} | errors={summary.Errors}");

                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Сканер"),
                    L.T(scanDirectory ? "Сканирование папки" : "Проверка файла"),
                    summary.Errors > 0 ? HistoryEvent.StatusFail : HistoryEvent.StatusOk,
                    L.T("Файлов: {0}, обнаружений: {1}", summary.FilesScanned, summary.Detections)));
            }
            else
            {
                StatusText = result.Code == -1
                    ? L.T("Сканирование отменено.")
                    : L.T("Ошибка: {0}", result.Message);
                _logger.Error($"SCAN | failed | rc={result.Code} | {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = L.T("Сканирование отменено.");
        }
        catch (Exception exception)
        {
            StatusText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("SCAN | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
