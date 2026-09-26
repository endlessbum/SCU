using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Services;

namespace SCU.ViewModels.Sections;

public partial class ComponentsViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly RuntimeInstaller _installer;
    private readonly IConfirmDialogService _dialogs;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    private ObservableCollection<ComponentItem> _components = new();

    [ObservableProperty]
    private ComponentItem? _selectedComponent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = L.T("Список компонентов не загружен.");

    public ComponentsViewModel(Logger logger, SCURunner runner, IConfirmDialogService dialogs)
    {
        _logger = logger;
        _installer = new RuntimeInstaller(logger, runner);
        _dialogs = dialogs;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление компонентов", async ct =>
        {
            StatusText = L.T("Определение установленных компонентов…");
            await Task.Yield();

            // Список строится целиком до замены: если чтение реестра упадёт,
            // прежний список останется на экране вместо пустого.
            IReadOnlyList<ComponentItem> built;
            try
            {
                built = _installer.BuildComponentList();
            }
            catch (Exception exception)
            {
                StatusText = L.T("Ошибка: {0}", exception.Message);
                _logger.Error("COMPONENTS | build list | " + exception);
                return;
            }

            Components.Clear();
            foreach (var component in built)
            {
                Components.Add(component);
            }

            StatusText = L.T("Загружено компонентов: {0}.", Components.Count);
            _logger.Info($"COMPONENTS | list loaded | count={Components.Count}");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(ComponentItem? component)
    {
        if (component is null)
        {
            return;
        }

        await RunExclusiveAsync($"установка {component.Name}", async ct =>
        {
            // П.22: кнопка скрывается и появляется круговой индикатор; измеримый
            // процент установки недоступен (внешние установщики) — indeterminate ring.
            component.IsInstalling = true;
            try
            {
                StatusText = L.T("Установка «{0}»…", component.Name);
                // Progress создаётся в UI-потоке — отчёты скачивания/проверки приходят в StatusText без Invoke.
                var progress = new Progress<string>(line => StatusText = L.S(line));
                var result = await _installer.InstallAsync(component, progress, ct).ConfigureAwait(true);

                if (result.IsSuccess)
                {
                    StatusText = L.S(result.Message);
                    _logger.Info($"COMPONENTS | installed: {component.Id}");

                    // Сервис выставил item.IsInstalled по факту установки (строку обновляет INPC);
                    // CanInstall читает item.IsInstalled и вне наблюдаемых свойств VM, поэтому
                    // команду оповещаем явно — кнопка «Установить» сразу гаснет.
                    InstallCommand.NotifyCanExecuteChanged();
                }
                else
                {
                    StatusText = L.T("Не удалось установить «{0}»: {1}", component.Name, result.Message);
                    _logger.Error($"COMPONENTS | install error for {component.Id}: {result.Message}");
                }
            }
            finally
            {
                component.IsInstalling = false;
            }
        }).ConfigureAwait(true);
    }

    // Тихое удаление установленного компонента официальным установщиком с /uninstall.
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync(ComponentItem? component)
    {
        if (component is null)
        {
            return;
        }

        if (!_dialogs.Ask(
                L.T("Удаление компонента"),
                L.T("Удалить «{0}»?\nБудет запущен официальный установщик в тихом режиме (/uninstall /quiet).", component.Name),
                L.T("Удалить")))
        {
            return;
        }

        await RunExclusiveAsync($"удаление {component.Name}", async ct =>
        {
            component.IsUninstalling = true;
            try
            {
                StatusText = L.T("Удаление «{0}»…", component.Name);
                var progress = new Progress<string>(line => StatusText = L.S(line));
                var result = await _installer.UninstallAsync(component, progress, ct).ConfigureAwait(true);

                if (result.IsSuccess)
                {
                    StatusText = L.S(result.Message);
                    _logger.Info($"COMPONENTS | uninstalled: {component.Id}");
                    UninstallCommand.NotifyCanExecuteChanged();
                }
                else
                {
                    StatusText = L.T("Не удалось удалить «{0}»: {1}", component.Name, result.Message);
                    _logger.Error($"COMPONENTS | uninstall error for {component.Id}: {result.Message}");
                }
            }
            finally
            {
                component.IsUninstalling = false;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | components operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        _operationCts?.Cancel();
    }

    private bool CanRefresh() => !IsBusy;
    private bool CanInstall(ComponentItem? item) => !IsBusy && item != null && !item.IsInstalled;
    private bool CanUninstall(ComponentItem? item) => !IsBusy && item != null && item.IsInstalled && item.CanUninstall;
    private bool CanCancel() => IsBusy;

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
            _logger.Error("COMPONENTS | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
