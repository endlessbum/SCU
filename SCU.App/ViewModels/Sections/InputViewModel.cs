using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Раздел 11 «Ввод, браузер и игры» — тумблеры поверх InputService
// (мышь, залипание клавиш, Edge, Game Bar, DVR, Game Mode) + пресет отключения Game Bar/DVR.
public partial class InputViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly InputService _inputService;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleSwitchCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableGameBarDvrCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isAdmin;

    [ObservableProperty]
    private string _statusText = L.T("Загрузка состояния…");

    public InputViewModel(Logger logger, IConfirmDialogService dialogs)
    {
        _logger = logger;
        _dialogs = dialogs;
        IsAdmin = Elevation.IsAdmin();
        _inputService = new InputService(logger, new RegistryHelper(logger));
        foreach (var option in _inputService.Switches)
        {
            // OnMeansEnable: тумблер включён = функция работает.
            Rows.Add(new SwitchRow(option.Id, option.Title, option.Description, false, onMeansEnable: true));
        }
    }

    public ObservableCollection<SwitchRow> Rows { get; } = [];

    public async Task InitializeAsync()
    {
        var states = await TaskRunner.RunBlocking(() =>
            _inputService.Switches
                .Select(option => (option.Id, IsOn: _inputService.IsSwitchOn(option)))
                .ToDictionary(x => x.Id, x => x.IsOn, StringComparer.OrdinalIgnoreCase))
            .ConfigureAwait(true);

        foreach (var row in Rows)
        {
            if (states.TryGetValue(row.Id, out var state))
            {
                row.ForceState(state);
            }
        }

        StatusText = L.T("Состояние обновлено.");
    }

    public bool IsInteractive => !IsBusy && IsAdmin;

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — изменение параметров ввода и игр недоступно.");

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

        var option = _inputService.Find(row.Id);
        if (option is null)
        {
            return;
        }

        var on = row.IsOn;
        await RunExclusiveAsync("переключение " + option.Id, async ct =>
        {
            StatusText = L.T(option.Title) + ": " + L.T(on ? "включение…" : "выключение…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _inputService.SetSwitch(option, on), ct).ConfigureAwait(true);
                var actual = await TaskRunner.RunBlocking(() => _inputService.IsSwitchOn(option), ct).ConfigureAwait(true);
                row.ForceState(actual);
                StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
                _logger.Info($"INPUT | {option.Id} | {(actual ? "on" : "off")} | rc={result.Code}");
            }
            catch (OperationCanceledException)
            {
                // Ресинк затронутой строки на пути отмены: чтение без токена, best-effort.
                try
                {
                    row.ForceState(await TaskRunner.RunBlocking(() => _inputService.IsSwitchOn(option), CancellationToken.None).ConfigureAwait(true));
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
    private async Task DisableGameBarDvrAsync()
    {
        if (!_dialogs.Ask(
                L.T("Game Bar и DVR"),
                L.T("Отключить Game Bar и фоновую запись (DVR)?\nGame Mode останется включённым — это полезно для игр."),
                L.T("Отключить")))
        {
            return;
        }

        await RunExclusiveAsync("отключение Game Bar / DVR", async ct =>
        {
            StatusText = L.T("Отключение Game Bar и DVR…");
            var result = await TaskRunner.RunBlocking(_inputService.DisableGameBarAndDvr, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            foreach (var row in Rows)
            {
                var option = _inputService.Find(row.Id);
                if (option is null)
                {
                    continue;
                }

                var state = await TaskRunner.RunBlocking(() => _inputService.IsSwitchOn(option), ct).ConfigureAwait(true);
                row.ForceState(state);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление раздела ввода", async ct =>
        {
            StatusText = L.T("Чтение параметров ввода и игр…");
            foreach (var row in Rows)
            {
                var option = _inputService.Find(row.Id);
                if (option is null)
                {
                    continue;
                }

                var state = await TaskRunner.RunBlocking(() => _inputService.IsSwitchOn(option), ct).ConfigureAwait(true);
                row.ForceState(state);
            }

            StatusText = L.T("Состояние обновлено.");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | input operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

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
            _logger.Error("INPUT | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
