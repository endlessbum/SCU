using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Services;

namespace SCU.ViewModels.Sections;

// Раздел 19 «Приложения» — полный список установленных программ из Uninstall-
// реестра (ARP). Удаление запускает родной деинсталлятор программы; список
// нужно обновить кнопкой после завершения удаления.
public partial class AppsViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly InstalledAppsService _service;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    private ObservableCollection<InstalledApp> _apps = new();

    // Поиск по разделу: фильтрует только список приложений (имя и издатель).
    [ObservableProperty]
    private string _appSearchText = string.Empty;

    // Поиск активен, пока клавиатурный фокус в поле (ставит AppsView):
    // тогда затемняется фон окна — как у поиска на «Главной».
    [ObservableProperty]
    private bool _isSearchFocused;

    public bool IsSearchActive => IsSearchFocused;

    partial void OnIsSearchFocusedChanged(bool value) => OnPropertyChanged(nameof(IsSearchActive));

    // Подпись над списком: «Программ найдено: N» — по текущему (отфильтрованному)
    // списку, пересчитывается при каждом изменении фильтра и обновлении реестра.
    [ObservableProperty]
    private string _appsFoundText = L.T("Программ найдено: {0}", 0);

    // Полный список из реестра: Apps — отфильтрованное представление.
    private IReadOnlyList<InstalledApp> _allApps = [];

    partial void OnAppSearchTextChanged(string value) => ApplyAppsFilter();

    private void ApplyAppsFilter()
    {
        var query = AppSearchText.Trim();
        Apps.Clear();
        IEnumerable<InstalledApp> matched = _allApps;
        if (query.Length > 0)
        {
            matched = matched.Where(app =>
                app.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (app.Publisher?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        foreach (var app in matched)
        {
            Apps.Add(app);
        }

        AppsFoundText = L.T("Программ найдено: {0}", Apps.Count);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = L.T("Список программ ещё не загружен.");

    public AppsViewModel(Logger logger, IConfirmDialogService dialogs)
    {
        _logger = logger;
        _dialogs = dialogs;
        _service = new InstalledAppsService(logger);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("чтение списка программ", async ct =>
        {
            StatusText = L.T("Чтение установленных программ…");
            // Чтение четырёх веток реестра — до нескольких тысяч разделов, уводим от UI.
            _allApps = await Task.Run(_service.GetInstalledApps, ct).ConfigureAwait(true);
            ApplyAppsFilter();

            StatusText = L.T("Программ найдено: {0}.", _allApps.Count);
            _logger.Info($"APPS | list loaded | count={Apps.Count}");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanUninstallApp))]
    private async Task UninstallAsync(InstalledApp? app)
    {
        if (app is null)
        {
            return;
        }

        if (!_dialogs.Ask(
                L.T("Удаление приложения"),
                L.T("Удалить «{0}» полностью?\n\nБудет запущен штатный тихий деинсталлятор; если он не завершится за отведённое время, процессы приложения будут сняты принудительно. Папка установки и папки данных удаляются отдельно, с показом списка перед удалением.\nДействие необратимо. Продолжить?", app.DisplayName),
                L.T("Удалить")))
        {
            return;
        }

        await RunExclusiveAsync($"удаление {app.DisplayName}", async ct =>
        {
            StatusText = L.T("Принудительное удаление «{0}»…", app.DisplayName);
            var result = await _service.UninstallCompletelyAsync(app, ct, folders =>
            {
                // Отдельное явное подтверждение удаления папок данных (п. 6
                // аудита): имя приложения — не доказательство владельца.
                var list = string.Join("\n", folders.Select(f => "• " + f));
                return Task.FromResult(_dialogs.Ask(
                    L.T("Удаление папок данных"),
                    L.T("Найдены папки данных, совпадающие по имени с приложением:\n{0}\n\nУдалить их?", list),
                    L.T("Удалить папки")));
            }).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);

            // Список перечитывается сразу: удалённое приложение исчезает без ручного обновления.
            if (result.IsSuccess)
            {
                _allApps = await Task.Run(_service.GetInstalledApps, CancellationToken.None).ConfigureAwait(true);
                ApplyAppsFilter();
            }
        }).ConfigureAwait(true);
    }

    private bool CanUninstallApp(InstalledApp? app) => !IsBusy && app != null && app.HasUninstaller;
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | apps operation");
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
            _logger.Error("APPS | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
