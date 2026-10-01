using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Раздел 10 «Интерфейс и проводник» — тумблеры поверх UIService, плюс действия:
// задержка меню (пресеты), перезапуск проводника и очистка панели задач (с подтверждением).
public partial class UIViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly UIService _uiService;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleSwitchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecommendedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetMenuDelayCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartExplorerCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTaskbarCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreTaskbarCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isAdmin;

    [ObservableProperty]
    private string _statusText = L.T("Загрузка состояния…");

    [ObservableProperty]
    private string _menuDelayText = string.Empty;

    // Подсветка чипов задержки меню: "20"/"400" при совпадении пресета, иное значение — null.
    [ObservableProperty]
    private string? _activeMenuDelayKey;

    private static string? MenuDelayKey(string menuDelay) => menuDelay.Trim() switch
    {
        "20" => "20",
        "400" => "400",
        _ => null
    };

    public UIViewModel(Logger logger, IConfirmDialogService dialogs)
    {
        _logger = logger;
        _dialogs = dialogs;
        IsAdmin = Elevation.IsAdmin();
        _uiService = new UIService(logger, new RegistryHelper(logger));
        foreach (var option in _uiService.ExplorerSwitches)
        {
            // OnMeansEnable: тумблер включён = функция работает (см. SwitchRow, п. семантики подписи).
            ExplorerRows.Add(new SwitchRow(option.Id, option.Title, option.Description, false, onMeansEnable: true));
        }

        foreach (var option in _uiService.VisualFxSwitches)
        {
            VisualFxRows.Add(new SwitchRow(option.Id, option.Title, option.Description, false, onMeansEnable: true));
        }

        var recommended = _uiService.GetRecommendedSwitch();
        RecommendedRow = new SwitchRow("recommended", recommended.Title, recommended.Description, false, onMeansEnable: true);
    }

    public ObservableCollection<SwitchRow> ExplorerRows { get; } = [];

    public async Task InitializeAsync()
    {
        var states = await TaskRunner.RunBlocking(() =>
        {
            var options = _uiService.ExplorerSwitches
                .Concat(_uiService.VisualFxSwitches)
                .Append(_uiService.GetRecommendedSwitch())
                .ToDictionary(option => option.Id, option => _uiService.IsSwitchOn(option), StringComparer.OrdinalIgnoreCase);
            return (states: options, menuDelay: _uiService.GetMenuShowDelay());
        }).ConfigureAwait(true);

        foreach (var row in ExplorerRows.Concat(VisualFxRows).Append(RecommendedRow))
        {
            if (states.states.TryGetValue(row.Id, out var state))
            {
                row.ForceState(state);
            }
        }

        MenuDelayText = states.menuDelay;
        ActiveMenuDelayKey = MenuDelayKey(states.menuDelay);
        StatusText = L.T("Состояние обновлено.");
    }

    public ObservableCollection<SwitchRow> VisualFxRows { get; } = [];

    public SwitchRow RecommendedRow { get; }

    public bool IsInteractive => !IsBusy && IsAdmin;

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — изменение параметров интерфейса недоступно.");

    private bool CanModify() => !IsBusy && IsAdmin;
    private bool CanRefresh() => !IsBusy;
    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleSwitchAsync(SwitchRow? row)
    {
        if (row is null)
        {
            return;
        }

        var on = row.IsOn;
        var option = FindOption(row.Id);
        if (option is null)
        {
            row.ForceState(!on);
            return;
        }

        await RunExclusiveAsync("переключение " + option.Id, async ct =>
        {
            StatusText = L.T(option.Title) + ": " + L.T(on ? "включение…" : "выключение…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _uiService.SetSwitch(option, on), ct).ConfigureAwait(true);
                var actual = await TaskRunner.RunBlocking(() => _uiService.IsSwitchOn(option), ct).ConfigureAwait(true);
                row.ForceState(actual);
                StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
                _logger.Info($"UI | {option.Id} | {(actual ? "on" : "off")} | rc={result.Code}");
            }
            catch (OperationCanceledException)
            {
                // Ресинк затронутой строки на пути отмены: чтение без токена, best-effort.
                try
                {
                    row.ForceState(await TaskRunner.RunBlocking(() => _uiService.IsSwitchOn(option), CancellationToken.None).ConfigureAwait(true));
                }
                catch
                {
                    // Не маскируем исходную отмену ошибкой чтения.
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleRecommendedAsync()
    {
        var on = RecommendedRow.IsOn;
        var option = _uiService.GetRecommendedSwitch();
        await RunExclusiveAsync("переключение recommended", async ct =>
        {
            StatusText = L.T(option.Title) + ": " + L.T(on ? "показ…" : "скрытие…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _uiService.SetSwitch(option, on), ct).ConfigureAwait(true);
                var actual = await TaskRunner.RunBlocking(() => _uiService.IsSwitchOn(option), ct).ConfigureAwait(true);
                RecommendedRow.ForceState(actual);
                StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
                _logger.Info($"UI | recommended | {(actual ? "on" : "off")} | rc={result.Code}");
            }
            catch (OperationCanceledException)
            {
                // Ресинк на пути отмены: чтение без токена, best-effort.
                try
                {
                    RecommendedRow.ForceState(await TaskRunner.RunBlocking(() => _uiService.IsSwitchOn(option), CancellationToken.None).ConfigureAwait(true));
                }
                catch
                {
                    // Не маскируем исходную отмену ошибкой чтения.
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task SetMenuDelayAsync(string? milliseconds)
    {
        if (!int.TryParse(milliseconds, out var ms))
        {
            return;
        }

        await RunExclusiveAsync("задержка меню", async ct =>
        {
            StatusText = L.T("Задержка меню: {0} мс…", ms);
            var result = await TaskRunner.RunBlocking(() => _uiService.SetMenuShowDelay(ms), ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            if (result.IsSuccess)
            {
                MenuDelayText = await TaskRunner.RunBlocking(_uiService.GetMenuShowDelay, ct).ConfigureAwait(true);
                ActiveMenuDelayKey = MenuDelayKey(MenuDelayText);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RestartExplorerAsync()
    {
        if (!_dialogs.Ask(
                L.T("Перезапуск проводника"),
                L.T("Перезапустить explorer.exe?\nПанель задач и открытые окна проводника на секунды исчезнут."),
                L.T("Перезапустить")))
        {
            return;
        }

        await RunExclusiveAsync("перезапуск проводника", async ct =>
        {
            StatusText = L.T("Перезапуск проводника…");
            var result = await TaskRunner.RunBlocking(_uiService.RestartExplorer, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ClearTaskbarAsync()
    {
        if (!_dialogs.Ask(
                L.T("Очистка панели задач…"),
                L.T("Удалить закреплённые значки и настройки панели задач?\nПроводник будет перезапущен. Вернуть их можно кнопкой «Восстановить панель задач»."),
                L.T("Очистить")))
        {
            return;
        }

        await RunExclusiveAsync("очистка панели задач", async ct =>
        {
            StatusText = L.T("Очистка панели задач…");
            var result = await TaskRunner.RunBlocking(_uiService.ClearTaskbar, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RestoreTaskbarAsync()
    {
        if (!_dialogs.Ask(
                L.T("Восстановление панели задач…"),
                L.T("Восстановить закрепления и настройки панели задач из бэкапа\n(снимок на момент последней очистки)? Проводник будет перезапущен."),
                L.T("Восстановить")))
        {
            return;
        }

        await RunExclusiveAsync("восстановление панели задач", async ct =>
        {
            StatusText = L.T("Восстановление панели задач…");
            var result = await TaskRunner.RunBlocking(_uiService.RestoreTaskbar, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление раздела UI", async ct =>
        {
            StatusText = L.T("Чтение параметров интерфейса…");
            foreach (var row in ExplorerRows.Concat(VisualFxRows).Append(RecommendedRow))
            {
                var option = FindOption(row.Id);
                if (option is null)
                {
                    continue;
                }

                var state = await TaskRunner.RunBlocking(() => _uiService.IsSwitchOn(option), ct).ConfigureAwait(true);
                row.ForceState(state);
            }

            MenuDelayText = await TaskRunner.RunBlocking(_uiService.GetMenuShowDelay, ct).ConfigureAwait(true);
            ActiveMenuDelayKey = MenuDelayKey(MenuDelayText);
            StatusText = L.T("Состояние обновлено. Изменения проводника применяются после перезапуска.");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | ui operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    private UiSwitchOption? FindOption(string id) =>
        _uiService.ExplorerSwitches.FirstOrDefault(o => o.Id == id)
        ?? _uiService.VisualFxSwitches.FirstOrDefault(o => o.Id == id)
        ?? (id == "recommended" ? _uiService.GetRecommendedSwitch() : null);

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
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
            _logger.Error("UI | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
