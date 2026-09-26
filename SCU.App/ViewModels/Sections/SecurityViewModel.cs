using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Services;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Раздел 13 «Безопасность (UAC)»: один тумблер «стандартный уровень / ослаблен».
// Ослабление — опасное направление, требует подтверждения (как ввод YES в BAT).
public partial class SecurityViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly SecurityService _securityService;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetStandardCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isAdmin;

    [ObservableProperty]
    private string _statusText = L.T("Загрузка состояния…");

    // Ручное свойство с уведомлением всегда: после операции фактическое значение
    // может совпасть со старым — тумблер обязан синхронизироваться.
    private bool _uacStandard;

    public bool UacStandard
    {
        get => _uacStandard;
        set
        {
            _uacStandard = value;
            OnPropertyChanged(nameof(UacStandard));
            OnPropertyChanged(nameof(UacActionText));
        }
    }

    // Постоянное описание UAC — не зависит от ON/OFF, без «Включить:/Отключить:».
    public string UacActionText =>
        System.Windows.Application.Current?.TryFindResource("S_UacDesc") as string ?? string.Empty;

    public SecurityViewModel(Logger logger, IConfirmDialogService dialogs)
    {
        _logger = logger;
        _dialogs = dialogs;
        IsAdmin = Elevation.IsAdmin();
        _securityService = new SecurityService(logger, new RegistryHelper(logger));
        // Подпись действия перечитывается при смене языка (живёт столько же, сколько приложение).
        L.LanguageChanged += RefreshActionText;
    }

    private void RefreshActionText() => OnPropertyChanged(nameof(UacActionText));

    public async Task InitializeAsync()
    {
        UacStandard = await TaskRunner.RunBlocking(_securityService.IsStandard).ConfigureAwait(true);
        StatusText = L.T("Состояние обновлено.");
    }

    public bool IsInteractive => !IsBusy && IsAdmin;

    private bool CanModify() => !IsBusy && IsAdmin;
    private bool CanRefresh() => !IsBusy;
    private bool CanCancel() => IsBusy;

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — изменение параметров UAC недоступно.");

    // Цель — новое значение свойства (связка TwoWay обновляет его до команды).
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task SetStandardAsync()
    {
        var standard = UacStandard;
        if (!standard && !_dialogs.Ask(
                L.T("Ослабление UAC"),
                L.T("Установить PromptOnSecureDesktop=0 и ConsentPromptBehaviorAdmin=0?\nЭто снижает защиту от повышения прав и считается опасной операцией.\nТекущие значения будут сохранены в резерв. Применяется после перезагрузки."),
                L.T("Ослабить")))
        {
            UacStandard = true;
            return;
        }

        await RunExclusiveAsync("UAC", async ct =>
        {
            StatusText = L.T(standard ? "Включение стандартного уровня UAC…" : "Ослабление UAC…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _securityService.SetStandard(standard), ct).ConfigureAwait(true);
                UacStandard = await TaskRunner.RunBlocking(_securityService.IsStandard, ct).ConfigureAwait(true);
                StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
                _logger.Info($"SECURITY | uac | {(UacStandard ? "standard" : "weak")} | rc={result.Code}");
            }
            catch (OperationCanceledException)
            {
                // Ресинк тумблера на пути отмены: чтение без токена, best-effort.
                try
                {
                    UacStandard = await TaskRunner.RunBlocking(_securityService.IsStandard, CancellationToken.None).ConfigureAwait(true);
                }
                catch
                {
                    // Не маскируем исходную отмену ошибкой чтения.
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление UAC", async ct =>
        {
            StatusText = L.T("Чтение параметров UAC…");
            UacStandard = await TaskRunner.RunBlocking(_securityService.IsStandard, ct).ConfigureAwait(true);
            StatusText = L.T(UacStandard
                ? "UAC на стандартном уровне Windows."
                : "UAC ослаблен (подтверждения выведены с безопасного рабочего стола).");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | security operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        L.LanguageChanged -= RefreshActionText;
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
            _logger.Error("SECURITY | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
