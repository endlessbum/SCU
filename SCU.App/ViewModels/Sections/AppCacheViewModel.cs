using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Infrastructure.Windows.Maintenance;
using SCU.Interop;
using SCU.Models;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Строка-карточка приложения в списке кэша: имя, объём временных данных,
// цели очистки и спиннер на время очистки этой строки.
public sealed partial class AppCacheRowViewModel : ObservableObject
{
    public AppCacheRowViewModel(AppCacheInfo info)
    {
        AppName = info.AppName;
        FolderPath = info.FolderPath;
        Targets = info.Targets;
        _sizeText = CleanupViewModel.FormatSize(info.TotalBytes);
    }

    public string AppName { get; }

    // Путь к папке приложения — подсказка над строкой (где именно живёт кэш).
    public string FolderPath { get; }

    // Кэш-каталоги приложения: замер остатка и очистка идут строго по ним.
    public IReadOnlyList<CleanupTarget> Targets { get; }

    [ObservableProperty]
    private string _sizeText;

    [ObservableProperty]
    private bool _isCleaning;
}

// Раздел 26 «Кэш приложений»: замер временных данных приложений при старте
// и по кнопке «Обновить», очистка по строкам с подтверждением. Модель —
// CleanupViewModel (RunExclusiveAsync, stale-guard замера, история операций).
public partial class AppCacheViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly AppCacheService _service;
    private readonly FileCleanupService _cleanupService;
    private readonly IConfirmDialogService _dialogs;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;

    // Счётчик запросов: результат устаревшего скана не должен перезаписать свежий.
    private int _measureVersion;

    public AppCacheViewModel(
        Logger logger,
        AppCacheService service,
        IConfirmDialogService dialogs,
        HistoryStore history)
    {
        _logger = logger;
        _service = service;
        _cleanupService = new FileCleanupService(logger);
        _dialogs = dialogs;
        _history = history;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanAppCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanAllCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = L.T("Сбор информации о временных данных приложений…");

    // Спиннер верхней карточки: скан идёт при старте и по «Обновить».
    // CleanAppCommand зависит от флага: очистку строки нельзя запускать во время
    // скана — RunExclusiveAsync диспозирует CTS, которым ещё живёт скан.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanAppCommand))]
    private bool _isScanning;

    public ObservableCollection<AppCacheRowViewModel> Apps { get; } = [];

    // ===================== Замер =====================

    // Замер при первом открытии вкладки (ленивая инициализация, аудит старта)
    // и при каждом открытии вкладки (OnCurrentSectionChanged): размеры кэша
    // меняются постоянно, кэшированный показ вводил бы в заблуждение.
    public async Task ActivateAsync()
    {
        if (IsBusy || IsScanning)
        {
            // Во время очистки список пересобирать нельзя: строка, которую
            // чистят, обязана дожить до конца операции. Повторный запуск при
            // идущем скане тоже не нужен (первое открытие после старта —
            // стартовый замер ещё идёт, дублировать его нечем и незачем).
            return;
        }

        var version = ++_measureVersion;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        IsScanning = true;
        StatusText = L.T("Сбор информации о временных данных приложений…");
        try
        {
            var infos = await _service.ScanAsync(_operationCts.Token).ConfigureAwait(true);
            if (version != _measureVersion)
            {
                return;
            }

            // ScanAsync возвращает список уже отсортированным по объёму (по убыванию).
            Apps.Clear();
            foreach (var info in infos)
            {
                Apps.Add(new AppCacheRowViewModel(info));
            }

            // CanExecute пакетной очистки зависит от Apps.Count — не только от IsBusy.
            CleanAllCommand.NotifyCanExecuteChanged();
            StatusText = infos.Count == 0
                ? L.T("Кэш приложений не обнаружен.")
                : L.T("Приложений с кэшем: {0}.", infos.Count);
        }
        catch (OperationCanceledException)
        {
            if (version == _measureVersion)
            {
                StatusText = L.T("Сбор информации отменён.");
            }
        }
        catch (Exception exception)
        {
            _logger.Error("APPCACHE | scan failed | " + exception);
            if (version == _measureVersion)
            {
                StatusText = L.T("Ошибка: {0}", exception.Message);
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool CanRefresh() => !IsBusy && !IsScanning;

    // Кнопка «Обновить»: тот же скан, что и при старте/открытии вкладки.
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        _logger.Info("APPCACHE | manual refresh");
        await ActivateAsync().ConfigureAwait(true);
    }

    // ===================== Очистка =====================

    // !IsScanning (как в CanCleanAll): очистка строки во время замера
    // дисPOзирует CTS живого скана и перезаписывает список посреди операции.
    private bool CanCleanApp(AppCacheRowViewModel? row) => !IsBusy && !IsScanning && row is not null;

    [RelayCommand(CanExecute = nameof(CanCleanApp))]
    private async Task CleanAppAsync(AppCacheRowViewModel? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        // Показываем, что именно будет удалено: только кэш-каталоги, не вся
        // папка приложения. Длинный список ужимается — полный остаётся в логе.
        var labels = row.Targets.Select(target => "• " + target.Label).ToList();
        var listed = labels.Count > 12
            ? string.Join("\n", labels.Take(12)) + "\n" + L.T("и ещё {0} кат.", labels.Count - 12)
            : string.Join("\n", labels);
        if (!_dialogs.Ask(
                L.T("Очистка кэша приложений"),
                L.T("Удалить временные данные «{0}»?\n{1}\nЗанятые файлы будут пропущены.", row.AppName, listed),
                L.T("Очистить")))
        {
            return;
        }

        _logger.Info("APPCACHE | clean start | " + row.AppName + " | targets=" + row.Targets.Count);
        // Результат замера, завершающегося во время очистки, не должен
        // перезаписывать список поверх удаляемой строки: инвалидируем устаревший
        // скан через счётчик версии (результат он вернёт — но будет отброшен).
        ++_measureVersion;
        await RunExclusiveAsync("очистка кэша " + row.AppName, async ct =>
        {
            StatusText = L.T("Очистка кэша «{0}»…", row.AppName);
            row.IsCleaning = true;
            try
            {
                var progress = new Progress<string>(line => StatusText = line);
                var result = await _cleanupService.CleanAsync(row.Targets, progress, ct).ConfigureAwait(true);
                StatusText = CleanupViewModel.FormatCleanupResult(result);

                if (result.IsSuccess && result.Value is { Count: > 0 })
                {
                    // BytesFreed — реально удалённые байты по целям, которые существовали.
                    var bytesFreed = result.Value.Where(item => item.Existed).Sum(item => item.BytesDeleted);
                    _history.Enqueue(new HistoryEvent(
                        DateTime.Now,
                        L.T("Очистка"),
                        L.T("Кэш приложений"),
                        HistoryEvent.StatusOk,
                        CleanupViewModel.FormatCleanupResult(result),
                        bytesFreed));
                    _logger.Info($"APPCACHE | cleaned | {row.AppName} | bytes={bytesFreed}");
                }

                // Остаток: если кэш кончился, строка уходит из списка. Порядок
                // не пересобирается: уменьшилась одна строка — сортировка по
                // убыванию не могла нарушиться.
                var remaining = await Task.Run(
                    () => FileCleanupService.MeasureBytes(row.Targets),
                    ct).ConfigureAwait(true);
                if (remaining <= 0)
                {
                    Apps.Remove(row);
                    if (Apps.Count == 0)
                    {
                        StatusText = L.T("Кэш приложений не обнаружен.");
                    }
                }
                else
                {
                    row.SizeText = CleanupViewModel.FormatSize(remaining);
                }
            }
            finally
            {
                row.IsCleaning = false;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | app cache scan");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Пакетная очистка всего найденного кэша (большой выключатель «Главной»):
    // одно подтверждение, цели всех карточек одной операцией.
    private bool CanCleanAll() => !IsBusy && !IsScanning && Apps.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCleanAll))]
    private async Task CleanAllAsync()
    {
        if (IsBusy || Apps.Count == 0)
        {
            return;
        }

        var rows = Apps.ToList();
        var targetCount = rows.Sum(row => row.Targets.Count);
        if (!_dialogs.Ask(
                L.T("Очистка кэша приложений"),
                L.T("Удалить временные данные приложений: {0}?\nЦелей очистки: {1}. Занятые файлы будут пропущены.",
                    string.Join(", ", rows.Select(row => "«" + row.AppName + "»")), targetCount),
                L.T("Очистить")))
        {
            return;
        }

        _logger.Info("APPCACHE | clean all start | apps=" + rows.Count + " | targets=" + targetCount);
        await RunExclusiveAsync("очистка кэша приложений (пакет)", async ct =>
        {
            StatusText = L.T("Очистка кэша приложений…");
            var progress = new Progress<string>(line => StatusText = line);
            var targets = rows.SelectMany(row => row.Targets).ToList();
            var result = await _cleanupService.CleanAsync(targets, progress, ct).ConfigureAwait(true);
            StatusText = CleanupViewModel.FormatCleanupResult(result);
            if (result.IsSuccess && result.Value is { Count: > 0 })
            {
                var bytesFreed = result.Value.Where(item => item.Existed).Sum(item => item.BytesDeleted);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Очистка"),
                    L.T("Кэш приложений"),
                    HistoryEvent.StatusOk,
                    CleanupViewModel.FormatCleanupResult(result),
                    bytesFreed));
                _logger.Info("APPCACHE | cleaned all | bytes=" + bytesFreed);
            }

            // Пересчёт строк: опустевшие уходят из списка, у остальных
            // обновляется объём. Порядок не пересобирается: строки только
            // уменьшались, сортировка по убыванию сохраняется.
            foreach (var row in rows)
            {
                row.IsCleaning = true;
                try
                {
                    var remaining = await Task.Run(
                        () => FileCleanupService.MeasureBytes(row.Targets),
                        ct).ConfigureAwait(true);
                    if (remaining <= 0)
                    {
                        Apps.Remove(row);
                    }
                    else
                    {
                        row.SizeText = CleanupViewModel.FormatSize(remaining);
                    }
                }
                finally
                {
                    row.IsCleaning = false;
                }
            }

            if (Apps.Count == 0)
            {
                StatusText = L.T("Кэш приложений не обнаружен.");
            }
        }).ConfigureAwait(true);
    }

    private bool CanCancel() => IsScanning || IsBusy;

    public void CancelOngoing() => _operationCts?.Cancel();

    public void Dispose()
    {
        _operationCts?.Cancel();
    }

    // Тот же контракт, что CleanupViewModel.RunExclusiveAsync: гвард повторного
    // входа, свой CTS, отмена и ошибки не покидают метод.
    private Task RunExclusiveAsync(string title, Func<CancellationToken, Task> action) =>
        SectionOperations.RunExclusiveAsync(title, "APPCACHE", () => IsBusy, value => IsBusy = value, () => _operationCts, value => _operationCts = value, value => StatusText = value, _logger, action);}
