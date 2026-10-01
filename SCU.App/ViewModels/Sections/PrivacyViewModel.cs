using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

public partial class PrivacyViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly SCURunner _runner;
    private readonly PrivacyService _privacyService;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCategoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = L.T("Показано текущее состояние категорий.");

    public PrivacyViewModel(Logger logger, SCURunner runner, IConfirmDialogService dialogs)
    {
        _logger = logger;
        _dialogs = dialogs;
        _runner = runner;
        IsAdmin = Elevation.IsAdmin();
        IsSCUAvailable = runner.IsAvailable;
        _privacyService = new PrivacyService(logger, new RegistryHelper(logger), new ServiceManager(), runner);

        foreach (var category in PrivacyService.Categories)
        {
            var row = new PrivacyRow(category, false);
            // Переключение любого тумблера меняет доступность «Включить все».
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(PrivacyRow.IsDisabled) or nameof(PrivacyRow.IsEnabled))
                {
                    EnableAllCommand.NotifyCanExecuteChanged();
                }
            };
            Rows.Add(row);
        }
    }

    public ObservableCollection<PrivacyRow> Rows { get; } = new();

    public async Task InitializeAsync()
    {
        var states = await TaskRunner.RunBlocking(() =>
            PrivacyService.Categories
                .ToDictionary(category => category.Id, category => _privacyService.IsCategoryApplied(category.Id), StringComparer.OrdinalIgnoreCase))
            .ConfigureAwait(true);

        foreach (var row in Rows)
        {
            if (states.TryGetValue(row.Category.Id, out var isDisabled))
            {
                row.ForceState(isDisabled);
            }
        }

        StatusText = L.T("Состояние категорий обновлено.");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _isAdmin;

    [ObservableProperty]
    private bool isSCUAvailable;

    // Для тумблеров: интерактивность = не занят + права администратора.
    public bool IsInteractive => !IsBusy && IsAdmin;

    public string ReadOnlyHint
    {
        get
        {
            if (!IsAdmin)
            {
                return L.T("Нужны права администратора — изменение параметров приватности недоступно.");
            }

            return string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление приватности", async ct =>
        {
            foreach (var row in Rows)
            {
                row.IsDisabled = await TaskRunner.RunBlocking(
                    () => _privacyService.IsCategoryApplied(row.Category.Id),
                    ct).ConfigureAwait(true);
            }

            StatusText = L.T("Состояние категорий обновлено.");
        }).ConfigureAwait(true);
    }

    // Тумблер строки: цель = новое состояние строки (связка TwoWay переключает его до команды).
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleCategoryAsync(PrivacyRow? row)
    {
        if (row is null)
        {
            return;
        }

        var enable = row.IsEnabled;

        if (!enable && !_dialogs.ConfirmChange(new DestructiveChange(
                L.T("Отключение — {0}", row.Category.Title),
                CurrentState: L.T("Категория «{0}» включена.", row.Category.Title),
                NewState: L.T("Категория отключена соответствующими твиками реестра/планировщика."),
                Consequences: L.T(row.Category.Description),
                Rollback: L.T("Текущие значения реестра сохраняются в бэкап; включение восстанавливает их."),
                ConfirmText: L.T("Отключить"))))
        {
            row.ForceState(!enable);
            return;
        }

        await RunExclusiveAsync((enable ? "включение " : "отключение ") + row.Category.Id, async ct =>
        {
            try
            {
                // CEIP-категория управляется задачами планировщика через PS, а не реестром.
                Result result;
                if (row.Category.Id == "ceip")
                {
                    StatusText = L.T(enable
                        ? "Восстановление задач CEIP из бэкапа…"
                        : "Отключение задач CEIP (через SCU.ps1)…");
                    result = enable
                        ? await _privacyService.RestoreCeipTasksAsync(_runner, ct).ConfigureAwait(true)
                        : await _privacyService.DisableCeipTasksAsync(_runner, ct).ConfigureAwait(true);
                    var actualCeip = await TaskRunner.RunBlocking(
                        () => _privacyService.IsCategoryApplied("ceip"),
                        ct).ConfigureAwait(true);
                    row.ForceState(actualCeip);
                    StatusText = result.IsSuccess
                        ? L.T(row.Category.Title) + L.T(actualCeip ? ": отключено. " : ": включено. ") + L.S(result.Message)
                        : L.T("{0}: ошибка (код {1}): {2}", L.T(row.Category.Title), result.Code, L.S(result.Message))
                          + (result.Code == 2 ? " " + L.T("Бэкап задач не создавался — включение недоступно.") : string.Empty);
                    _logger.Info($"PRIVACY | ceip | {(actualCeip ? "disabled" : "enabled")} | rc={result.Code}");
                    return;
                }

                StatusText = L.T(enable ? "Включение: " : "Отключение: ") + L.T(row.Category.Title) + "…";
                result = enable
                    ? await _privacyService.EnableAsync(row.Category.Id, ct).ConfigureAwait(true)
                    : await _privacyService.DisableAsync(row.Category.Id, ct).ConfigureAwait(true);

                // Фактическое состояние после операции — источник правды для тумблера.
                var actual = await TaskRunner.RunBlocking(
                    () => _privacyService.IsCategoryApplied(row.Category.Id),
                    ct).ConfigureAwait(true);
                row.ForceState(actual);

                if (result.IsSuccess)
                {
                    StatusText = L.T(row.Category.Title) + L.T(actual ? ": отключено. " : ": включено. ") + L.S(result.Message);
                    _logger.Info($"PRIVACY | {row.Category.Id} | {(actual ? "disabled" : "enabled")}");
                }
                else
                {
                    StatusText = L.T("{0}: ошибка (код {1}): {2}", L.T(row.Category.Title), result.Code, L.S(result.Message));
                    _logger.Error($"PRIVACY | {row.Category.Id} | rc={result.Code} | {result.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                // Ресинк затронутой строки на пути отмены: чтение без токена, best-effort.
                try
                {
                    var actual = await TaskRunner.RunBlocking(
                        () => _privacyService.IsCategoryApplied(row.Category.Id),
                        CancellationToken.None).ConfigureAwait(true);
                    row.ForceState(actual);
                }
                catch
                {
                    // Не маскируем исходную отмену ошибкой чтения.
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    // «Включить все»: отключает перечисленные ниже категории
    // (выключение = снятие твиков). Кнопка активна, пока хотя бы один
    // переключатель выключен; когда включены все — неактивна.
    private bool CanEnableAll() => CanModify() && Rows.Any(row => row.IsDisabled);

    [RelayCommand(CanExecute = nameof(CanEnableAll))]
    private async Task EnableAllAsync()
    {
        var disabled = Rows.Where(row => row.IsDisabled).ToList();
        if (disabled.Count == 0)
        {
            return;
        }

        if (!_dialogs.ConfirmChange(new DestructiveChange(
                L.T("Включение категорий приватности"),
                CurrentState: L.T("{0} категорий отключены:", disabled.Count)
                    + "\n" + string.Join(", ", disabled.Select(row => L.T(row.Category.Title))),
                NewState: L.T("Все перечисленные категории вернутся в рабочее состояние."),
                Consequences: L.T("Отключённые SCU функции телеметрии/слежения снова активируются штатными параметрами Windows."),
                Rollback: L.T("Значения восстанавливаются из бэкапа, а при его отсутствии — к заводским."),
                ConfirmText: L.T("Включить все"))))
        {
            return;
        }

        await RunExclusiveAsync("включение всех категорий", async ct =>
        {
            var failures = new List<string>();
            foreach (var row in disabled)
            {
                StatusText = L.T("Включение: ") + L.T(row.Category.Title) + "…";
                var result = await _privacyService.EnableAsync(row.Category.Id, ct).ConfigureAwait(true);
                // Фактическое состояние после операции — источник правды для тумблера.
                var actual = await TaskRunner.RunBlocking(
                    () => _privacyService.IsCategoryApplied(row.Category.Id),
                    ct).ConfigureAwait(true);
                row.ForceState(actual);

                if (result.IsSuccess)
                {
                    _logger.Info("PRIVACY | enable all | " + row.Category.Id + " ok");
                }
                else
                {
                    failures.Add(L.T("{0}: {1}", L.T(row.Category.Title), L.S(result.Message)));
                    _logger.Error("PRIVACY | enable all | " + row.Category.Id + " | rc=" + result.Code + " | " + result.Message);
                }
            }

            if (failures.Count == 0)
            {
                StatusText = L.T("Все категории включены.");
            }
            else
            {
                StatusText = L.T("Включение категорий: часть операций не удалась — {0}", string.Join("; ", failures.Select(L.S)));
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | privacy operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        _operationCts?.Cancel();
    }

    private bool CanModify() => !IsBusy && IsAdmin;
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
            _logger.Error("PRIVACY | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public partial class PrivacyRow : ObservableObject
    {
        private bool _isDisabled;

        public PrivacyRow(PrivacyService.Category category, bool isDisabled)
        {
            Category = category;
            _isDisabled = isDisabled;
            // П.28: подпись действия перечитывается при смене языка. Строка живёт
            // столько же, сколько раздел (приложение) — отписка не требуется.
            L.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged() => OnPropertyChanged(nameof(ActionDescription));

        public PrivacyService.Category Category { get; }

        public string ToolTipText =>
            Category.Description + L.T("\nОтключайте только те категории, последствия которых вам понятны; после операции исходные значения сохраняются в бэкап.");

        public bool IsDisabled
        {
            get => _isDisabled;
            set
            {
                if (SetProperty(ref _isDisabled, value))
                {
                    OnPropertyChanged(nameof(IsEnabled));
                    OnPropertyChanged(nameof(ActionDescription));
                }
            }
        }

        // Для тумблера: true = категория работает (не отключена твиками).
        public bool IsEnabled
        {
            get => !_isDisabled;
            set => IsDisabled = !value;
        }

        // Принудительная синхронизация тумблера с фактическим состоянием (даже если не менялось).
        public void ForceState(bool isDisabled)
        {
            _isDisabled = isDisabled;
            OnPropertyChanged(nameof(IsDisabled));
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(ActionDescription));
        }

        // Постоянное описание категории: не зависит от ON/OFF и не использует
        // запрещённые формулировки «Включить:» / «Отключить:».
        public string ActionDescription => L.T(Category.Description ?? string.Empty);
    }
}
