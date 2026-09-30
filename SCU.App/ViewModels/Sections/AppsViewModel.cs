using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;

namespace SCU.ViewModels.Sections;

// Режим сортировки вкладки «Приложения»; порядок элементов SortOptions в VM
// соответствует порядку значений (опции — локализованные строки).
internal enum AppSortMode
{
    Name,
    Size,
    InstallDate,
}

// Раздел 19 «Приложения» — полный список установленных программ из Uninstall-
// реестра (ARP). Удаление запускает родной деинсталлятор программы; список
// нужно обновить кнопкой после завершения удаления.
public partial class AppsViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly InstalledAppsService _service;
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _sizesCts;

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

    // ===================== Сортировка =====================

    private AppSortMode _sortMode = AppSortMode.Name;

    // Опции сортировки — локализованные строки; порядок списка соответствует
    // порядку AppSortMode. Пересобираются при смене языка (как в «Истории»).
    public ObservableCollection<string> SortOptions { get; } = [];

    [ObservableProperty]
    private string? _selectedSort;

    // Направление: по возрастанию (А→Я, малый→большой, старые→новые) — по умолчанию.
    [ObservableProperty]
    private bool _sortAscending = true;

    public string SortDirectionGlyph => SortAscending ? "↑" : "↓";

    public string SortDirectionTooltip => SortAscending
        ? L.T("По возрастанию")
        : L.T("По убыванию");

    partial void OnSelectedSortChanged(string? value)
    {
        var index = SortOptions.IndexOf(value ?? string.Empty);
        if (index >= 0)
        {
            _sortMode = (AppSortMode)index;
        }

        ApplyAppsFilter();
    }

    partial void OnSortAscendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortDirectionGlyph));
        OnPropertyChanged(nameof(SortDirectionTooltip));
        ApplyAppsFilter();
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortAscending = !SortAscending;

    private void RebuildSortOptions()
    {
        var selected = SelectedSort;
        SortOptions.Clear();
        SortOptions.Add(L.T("По имени"));
        SortOptions.Add(L.T("По размеру"));
        SortOptions.Add(L.T("По дате установки"));
        SelectedSort = selected is not null && SortOptions.Contains(selected)
            ? selected
            : SortOptions[0];
    }

    // ===================== /Сортировка =====================

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

        matched = SortApps(matched, _sortMode, SortAscending);

        foreach (var app in matched)
        {
            Apps.Add(app);
        }

        AppsFoundText = L.T("Программ найдено: {0}", Apps.Count);
    }

    // Сортировка. Объём и дата: записи без значения (размер ещё считается,
    // даты нет в реестре) всегда в конце, независимо от направления.
    internal static IEnumerable<InstalledApp> SortApps(
        IEnumerable<InstalledApp> apps, AppSortMode mode, bool ascending) =>
        (mode, ascending) switch
        {
            (AppSortMode.Size, true) => apps
                .OrderByDescending(app => app.SizeBytes.HasValue)
                .ThenBy(app => app.SizeBytes ?? 0),
            (AppSortMode.Size, false) => apps
                .OrderByDescending(app => app.SizeBytes.HasValue)
                .ThenByDescending(app => app.SizeBytes ?? 0),
            (AppSortMode.InstallDate, true) => apps
                .OrderByDescending(app => app.InstallDate.HasValue)
                .ThenBy(app => app.InstallDate ?? DateTime.MinValue),
            (AppSortMode.InstallDate, false) => apps
                .OrderByDescending(app => app.InstallDate.HasValue)
                .ThenByDescending(app => app.InstallDate ?? DateTime.MinValue),
            (_, true) => apps.OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            (_, false) => apps.OrderByDescending(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        };

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
        RebuildSortOptions();
        L.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        _uiDispatcher.Invoke(RebuildSortOptions);
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
            StartSizeComputation();

            StatusText = L.T("Программ найдено: {0}.", _allApps.Count);
            _logger.Info($"APPS | list loaded | count={Apps.Count}");
        }).ConfigureAwait(true);
    }

    // Фоновый расчёт размеров приложений: фактический — суммой файлов папки
    // установки, без неё — заявленный установщиком EstimatedSize из реестра.
    // Суммирование сотен папок может идти заметное время, поэтому карточки
    // обновляются по мере готовности (INPC у InstalledApp), а повторное
    // обновление списка отменяет предыдущий расчёт.
    private void StartSizeComputation()
    {
        _sizesCts?.Cancel();
        _sizesCts?.Dispose();
        _sizesCts = new CancellationTokenSource();
        var ct = _sizesCts.Token;
        var apps = _allApps.ToList();

        _ = Task.Run(() =>
        {
            try
            {
                apps.AsParallel()
                    .WithCancellation(ct)
                    .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
                    .ForAll(app =>
                    {
                        var actual = _service.GetInstallSizeBytes(app);
                        var bytes = actual ?? (app.EstimatedSizeKb is { } kb ? kb * 1024L : null);
                        if (bytes is { } size)
                        {
                            app.SizeBytes = size;
                            app.SizeText = InstalledAppsService.FormatSizeBytes(size);
                        }
                    });

                _logger.Info($"APPS | sizes computed | count={apps.Count}");

                // Сортировка по объёму должна отражать готовые размеры: список
                // перестраивается один раз после окончания расчёта.
                if (_sortMode == AppSortMode.Size)
                {
                    _uiDispatcher.Invoke(ApplyAppsFilter);
                }
            }
            catch (OperationCanceledException)
            {
                // Список обновился — расчёт заменён новым.
            }
            catch (Exception exception)
            {
                _logger.Warn("APPS | size computation failed | " + exception.Message);
            }
        }, CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanUninstallApp))]
    private async Task UninstallAsync(InstalledApp? app)
    {
        if (app is null)
        {
            return;
        }

        if (!_dialogs.ConfirmChange(new DestructiveChange(
                L.T("Удаление приложения"),
                CurrentState: L.T("«{0}» установлено и работает.", app.DisplayName),
                NewState: L.T("Приложение полностью удалено с компьютера."),
                Consequences: L.T("Будет запущен штатный тихий деинсталлятор; если он не завершится за отведённое время, процессы приложения будут сняты принудительно. Папка установки и папки данных удаляются отдельно, с показом списка перед удалением."),
                Rollback: null,
                ConfirmText: L.T("Удалить"))))
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
        _sizesCts?.Cancel();
        L.LanguageChanged -= OnLanguageChanged;
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
