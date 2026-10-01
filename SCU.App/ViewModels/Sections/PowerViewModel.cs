using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Views.Controls;

namespace SCU.ViewModels.Sections;

// Раздел 8 «Питание, память и CPU» — аналог :PerfMenu из Utilities.bat.
// Изменяющие операции: план (powercfg), гибернация (powercfg), быстрый запуск (реестр с бэкапом),
// файл подкачки (WMI+реестр), numproc/truncatememory (bcdedit с повторной проверкой).
public partial class PowerViewModel : ObservableObject, IDisposable, ISectionOperationCancellable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly PowerService _powerService;
    private readonly RegistryHelper _registry;
    private readonly ServiceManager _serviceManager;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetPlanHighCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetPlanUltimateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetPlanBalancedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetPlanPowerSaverCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetPlanBitsumCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleHibernationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleFastBootCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetPageFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetSystemManagedPageFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearCpuLimitsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleMemoryCompressionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleSysMainCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleShortNamesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleLastAccessCommand))]
    [NotifyCanExecuteChangedFor(nameof(TogglePrefetcherCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    [NotifyPropertyChangedFor(nameof(CanToggleFastBoot))]
    [NotifyPropertyChangedFor(nameof(CanToggleMemoryCompression))]
    [NotifyPropertyChangedFor(nameof(CanToggleShortNames))]
    [NotifyPropertyChangedFor(nameof(CanToggleLastAccess))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    [NotifyPropertyChangedFor(nameof(CanToggleFastBoot))]
    [NotifyPropertyChangedFor(nameof(CanToggleMemoryCompression))]
    [NotifyPropertyChangedFor(nameof(CanToggleShortNames))]
    [NotifyPropertyChangedFor(nameof(CanToggleLastAccess))]
    private bool _isAdmin;

    // Для тумблеров: интерактивность = не занят + права администратора.
    public bool IsInteractive => !IsBusy && IsAdmin;

    [ObservableProperty]
    private string _statusText = L.T("Загрузка состояния…");

    // GUID активной схемы (для подсветки чипов в шапке) и вычисляемый ключ вида
    // "high"/"ultimate"/… — сопоставление со стабильными GUID шаблонов и их копий.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePlanKey))]
    private string? _activePlanGuid;

    public string? ActivePlanKey => ActivePlanGuid switch
    {
        null => null,
        _ when string.Equals(ActivePlanGuid, PowerService.HighPerformancePlanGuid, StringComparison.OrdinalIgnoreCase) => "high",
        _ when string.Equals(ActivePlanGuid, PowerService.UltimatePerformancePlanGuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ActivePlanGuid, PowerService.UltimatePerformanceCopyGuid, StringComparison.OrdinalIgnoreCase) => "ultimate",
        _ when string.Equals(ActivePlanGuid, PowerService.BalancedPlanGuid, StringComparison.OrdinalIgnoreCase) => "balanced",
        _ when string.Equals(ActivePlanGuid, PowerService.PowerSaverPlanGuid, StringComparison.OrdinalIgnoreCase) => "powersaver",
        _ when string.Equals(ActivePlanGuid, PowerService.BitsumHighestPerformancePlanGuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ActivePlanGuid, PowerService.BitsumHighestPerformanceCopyGuid, StringComparison.OrdinalIgnoreCase) => "bitsum",
        _ => null
    };

    // Ключ активного пресета файла подкачки ("6144"/"8192"/…/"system"), null — не совпало.
    [ObservableProperty]
    private string? _activePageFileKey;

    /// <summary>Текстовая сводка: режим, тома, размеры, crash dump.</summary>
    [ObservableProperty]
    private string _pageFileSummaryText = string.Empty;

    /// <summary>Индикаторы effective state: пусто / «?» / «*» / «≠».</summary>
    [ObservableProperty]
    private string _fastBootStateBadge = string.Empty;

    [ObservableProperty]
    private string _memoryCompressionStateBadge = string.Empty;

    [ObservableProperty]
    private string _shortNamesStateBadge = string.Empty;

    [ObservableProperty]
    private string _shortNamesDetailText = string.Empty;

    [ObservableProperty]
    private string _lastAccessStateBadge = string.Empty;

    [ObservableProperty]
    private string _cpuLimitsText = string.Empty;

    /// <summary>Формат бейджа: Unknown→?, PendingReboot→*, Mixed→≠, иначе пусто.</summary>
    internal static string FormatStateBadge(SystemSettingState state) =>
        state switch
        {
            SystemSettingState.Unknown => "?",
            SystemSettingState.Unavailable => "—",
            SystemSettingState.PendingReboot => "*",
            SystemSettingState.Mixed => "≠",
            SystemSettingState.PartiallyEnabled => "≠",
            _ => string.Empty
        };

    /// <summary>
    /// Тумблер допустим только при однозначном или отложенном состоянии.
    /// Mixed / Unknown / Unavailable — не бинарим в ON/OFF.
    /// </summary>
    internal static bool IsToggleableState(SystemSettingState state) =>
        state is SystemSettingState.Enabled
            or SystemSettingState.Disabled
            or SystemSettingState.PendingReboot;

    // Effective state (не bool) — источник истины для бейджа и CanToggle*.
    private SystemSettingState _fastBootState = SystemSettingState.Unknown;
    private SystemSettingState _memoryCompressionState = SystemSettingState.Unknown;
    private SystemSettingState _shortNamesState = SystemSettingState.Unknown;
    private SystemSettingState _lastAccessState = SystemSettingState.Unknown;

    public SystemSettingState FastBootState
    {
        get => _fastBootState;
        private set
        {
            if (_fastBootState == value) return;
            _fastBootState = value;
            OnPropertyChanged(nameof(FastBootState));
            OnPropertyChanged(nameof(CanToggleFastBoot));
        }
    }

    public SystemSettingState MemoryCompressionState
    {
        get => _memoryCompressionState;
        private set
        {
            if (_memoryCompressionState == value) return;
            _memoryCompressionState = value;
            OnPropertyChanged(nameof(MemoryCompressionState));
            OnPropertyChanged(nameof(CanToggleMemoryCompression));
        }
    }

    public SystemSettingState ShortNamesState
    {
        get => _shortNamesState;
        private set
        {
            if (_shortNamesState == value) return;
            _shortNamesState = value;
            OnPropertyChanged(nameof(ShortNamesState));
            OnPropertyChanged(nameof(CanToggleShortNames));
        }
    }

    public SystemSettingState LastAccessState
    {
        get => _lastAccessState;
        private set
        {
            if (_lastAccessState == value) return;
            _lastAccessState = value;
            OnPropertyChanged(nameof(LastAccessState));
            OnPropertyChanged(nameof(CanToggleLastAccess));
        }
    }

    public bool CanToggleFastBoot => IsInteractive && IsToggleableState(FastBootState);
    public bool CanToggleMemoryCompression => IsInteractive && IsToggleableState(MemoryCompressionState);
    public bool CanToggleShortNames => IsInteractive && IsToggleableState(ShortNamesState);
    public bool CanToggleLastAccess => IsInteractive && IsToggleableState(LastAccessState);

    // Состояния тумблеров. Ручные свойства с уведомлением всегда:
    // после операции фактическое значение может совпасть со старым — тумблер обязан синхронизироваться.
    private bool _hibernationEnabled;

    public bool HibernationEnabled
    {
        get => _hibernationEnabled;
        set
        {
            _hibernationEnabled = value;
            OnPropertyChanged(nameof(HibernationEnabled));
            OnPropertyChanged(nameof(HibernationActionText));
        }
    }

    private bool _fastBootEnabled;

    public bool FastBootEnabled
    {
        get => _fastBootEnabled;
        set
        {
            _fastBootEnabled = value;
            OnPropertyChanged(nameof(FastBootEnabled));
            OnPropertyChanged(nameof(FastBootActionText));
        }
    }

    private bool _memoryCompressionEnabled;

    public bool MemoryCompressionEnabled
    {
        get => _memoryCompressionEnabled;
        set
        {
            _memoryCompressionEnabled = value;
            OnPropertyChanged(nameof(MemoryCompressionEnabled));
            OnPropertyChanged(nameof(MemoryCompressionActionText));
        }
    }

    private bool _sysMainEnabled;

    public bool SysMainEnabled
    {
        get => _sysMainEnabled;
        set
        {
            _sysMainEnabled = value;
            OnPropertyChanged(nameof(SysMainEnabled));
            OnPropertyChanged(nameof(SysMainActionText));
        }
    }

    private bool _shortNamesEnabled;

    public bool ShortNamesEnabled
    {
        get => _shortNamesEnabled;
        set
        {
            _shortNamesEnabled = value;
            OnPropertyChanged(nameof(ShortNamesEnabled));
            OnPropertyChanged(nameof(ShortNamesActionText));
        }
    }

    private bool _lastAccessEnabled;

    public bool LastAccessEnabled
    {
        get => _lastAccessEnabled;
        set
        {
            _lastAccessEnabled = value;
            OnPropertyChanged(nameof(LastAccessEnabled));
            OnPropertyChanged(nameof(LastAccessActionText));
        }
    }

    private bool _prefetcherEnabled;

    public bool PrefetcherEnabled
    {
        get => _prefetcherEnabled;
        set
        {
            _prefetcherEnabled = value;
            OnPropertyChanged(nameof(PrefetcherEnabled));
            OnPropertyChanged(nameof(PrefetcherActionText));
        }
    }

    // ===================== Подписи тумблеров (п.28) =====================
    // Постоянное описание функции — НЕ зависит от ON/OFF и НЕ содержит
    // «Включить:» / «Отключить:». Текст из словаря строк XAML.

    private static string PermanentDesc(string resourceKey) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as string ?? resourceKey;

    public string HibernationActionText => PermanentDesc("S_PowerHibernationDesc");

    public string FastBootActionText => PermanentDesc("S_PowerFastBootDesc");

    public string MemoryCompressionActionText => PermanentDesc("S_PowerMemCompressionDesc");

    public string SysMainActionText => PermanentDesc("S_PowerSysMainDesc");

    public string ShortNamesActionText => PermanentDesc("S_Power8dot3Desc");

    public string LastAccessActionText => PermanentDesc("S_PowerLastAccessDesc");

    public string PrefetcherActionText => PermanentDesc("S_PowerPrefetcherDesc");

    private void RefreshActionTexts()
    {
        OnPropertyChanged(nameof(HibernationActionText));
        OnPropertyChanged(nameof(FastBootActionText));
        OnPropertyChanged(nameof(MemoryCompressionActionText));
        OnPropertyChanged(nameof(SysMainActionText));
        OnPropertyChanged(nameof(ShortNamesActionText));
        OnPropertyChanged(nameof(LastAccessActionText));
        OnPropertyChanged(nameof(PrefetcherActionText));
    }

    public PowerViewModel(Logger logger, LongProcessRunner runner, IConfirmDialogService dialogs, HistoryStore history)
    {
        _logger = logger;
        _dialogs = dialogs;
        _history = history;
        IsAdmin = Elevation.IsAdmin();
        _registry = new RegistryHelper(logger);
        _powerService = new PowerService(logger, runner, _registry);
        _serviceManager = new ServiceManager();
        // Подписи действия перечитываются при смене языка (живёт столько же, сколько приложение).
        L.LanguageChanged += RefreshActionTexts;
    }

    public string ReadOnlyHint =>
        IsAdmin ? string.Empty : L.T("Нужны права администратора — изменение параметров питания недоступно.");

    private bool CanModify() => !IsBusy && IsAdmin;
    private bool CanRefresh() => !IsBusy;
    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunExclusiveAsync("обновление состояния питания", async ct =>
        {
            StatusText = L.T("Чтение состояния (powercfg, bcdedit, реестр)…");
            // Флаг вместо сравнения локализованной строки: префикс «Чтение» в EN не совпадает никогда.
            var statusIsReading = true;

            await RefreshPlansAsync(ct).ConfigureAwait(true);

            var pageState = await _powerService.GetPageFileStateAsync(ct).ConfigureAwait(true);
            ApplyPageFileState(pageState);

            var cpu = await _powerService.GetCpuLimitsSummaryAsync(ct).ConfigureAwait(true);
            CpuLimitsText = cpu.IsSuccess ? cpu.Value ?? string.Empty : "bcdedit: " + cpu.Message;

            // Fast Startup: effective state через powercfg + реестр, не только HiberbootEnabled.
            var fastStartup = await _powerService.GetFastStartupStateAsync(ct).ConfigureAwait(true);
            if (fastStartup.IsSuccess && fastStartup.Value is not null)
            {
                var info = fastStartup.Value;
                var fs = info.State;
                FastBootState = fs;
                // PendingReboot: тумблер отражает целевое (записанное) значение HiberbootEnabled.
                if (fs is SystemSettingState.Enabled or SystemSettingState.Disabled)
                    FastBootEnabled = fs == SystemSettingState.Enabled;
                else if (fs == SystemSettingState.PendingReboot && info.HiberbootEnabled is int hb)
                    FastBootEnabled = hb != 0;
                FastBootStateBadge = FormatStateBadge(fs);
            }
            else
            {
                FastBootState = SystemSettingState.Unknown;
                FastBootStateBadge = "?";
                StatusText = L.T("Быстрый запуск: {0}", fastStartup.Message);
                statusIsReading = false;
            }

            // 8.3: per-volume через fsutil при необходимости.
            var shortAsync = await _powerService.GetShortNamesInfoAsync(ct).ConfigureAwait(true);
            var shortInfo = shortAsync.IsSuccess && shortAsync.Value is not null
                ? shortAsync.Value
                : PowerService.GetShortNamesInfo();
            ShortNamesState = shortInfo.EffectiveState;
            ShortNamesStateBadge = FormatStateBadge(shortInfo.EffectiveState);
            if (shortInfo.GlobalMode is ShortNameGlobalMode.EnabledForAll or ShortNameGlobalMode.DisabledForAll)
                ShortNamesEnabled = shortInfo.GlobalMode == ShortNameGlobalMode.EnabledForAll;
            if (shortInfo.VolumeStates is { Count: > 0 })
            {
                ShortNamesDetailText = string.Join(" · ",
                    shortInfo.VolumeStates.Select(v =>
                        $"{v.Volume.TrimEnd('\\')}: {(v.Enabled ? L.T("вкл") : L.T("выкл"))}"));
            }
            else
            {
                ShortNamesDetailText = shortInfo.GlobalMode switch
                {
                    ShortNameGlobalMode.EnabledForAll => L.T("Общий режим: для всех томов"),
                    ShortNameGlobalMode.DisabledForAll => L.T("Общий режим: отключено для всех"),
                    ShortNameGlobalMode.PerVolume => L.T("Общий режим: настраивается для каждого тома"),
                    ShortNameGlobalMode.DisabledExceptSystem => L.T("Общий режим: отключено кроме системного"),
                    _ => L.T("Общий режим: неизвестно")
                };
            }

            // Last Access: registry + fsutil (расхождение → PendingReboot).
            var lastAccessResult = await _powerService.GetLastAccessInfoAsync(ct).ConfigureAwait(true);
            var lastInfo = lastAccessResult.IsSuccess && lastAccessResult.Value is not null
                ? lastAccessResult.Value
                : PowerService.GetLastAccessInfo();
            LastAccessState = lastInfo.State;
            if (lastInfo.State is SystemSettingState.Enabled or SystemSettingState.Disabled)
                LastAccessEnabled = lastInfo.State == SystemSettingState.Enabled;
            else if (lastInfo.State == SystemSettingState.PendingReboot && lastInfo.RawValue is int rawLa)
                LastAccessEnabled = (rawLa & 1) == 0;
            LastAccessStateBadge = FormatStateBadge(lastInfo.State);

            var states = await TaskRunner.RunBlocking(
                () => (
                    hibernation: PowerService.TryIsHibernationEnabled(),
                    prefetcher: PowerService.TryIsPrefetcherEnabled(),
                    sysMain: PowerService.TryIsSysMainEnabled()),
                ct).ConfigureAwait(true);

            if (states.hibernation is bool hib)
                HibernationEnabled = hib;
            if (states.prefetcher is bool pf)
                PrefetcherEnabled = pf;
            if (states.sysMain is bool sm)
                SysMainEnabled = sm;

            var compression = await _powerService.GetMemoryCompressionAsync(ct).ConfigureAwait(true);
            if (compression.IsSuccess)
            {
                MemoryCompressionEnabled = compression.Value;
                MemoryCompressionState = compression.Value
                    ? SystemSettingState.Enabled
                    : SystemSettingState.Disabled;
                MemoryCompressionStateBadge = string.Empty;
            }
            else
            {
                // Не переключаем bool в false: оставляем прежнее значение, бейдж Unknown.
                MemoryCompressionState = SystemSettingState.Unknown;
                MemoryCompressionStateBadge = "?";
                StatusText = L.T("Сжатие памяти: состояние неизвестно ({0}).", compression.Message);
                statusIsReading = false;
            }

            if (statusIsReading || string.IsNullOrEmpty(StatusText))
                StatusText = L.T("Состояние обновлено.");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task SetPlanHighAsync() => SetPlanAsync(PowerService.HighPerformancePlanGuid, "Высокая производительность");

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task SetPlanUltimateAsync() => SetPlanAsync(PowerService.UltimatePerformancePlanGuid, "Максимальная производительность");

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task SetPlanBalancedAsync() => SetPlanAsync(PowerService.BalancedPlanGuid, "Сбалансированный");

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task SetPlanPowerSaverAsync() => SetPlanAsync(PowerService.PowerSaverPlanGuid, "Экономия энергии");

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task SetPlanBitsumAsync() => SetPlanAsync(PowerService.BitsumHighestPerformancePlanGuid, "Bitsum Highest Performance");

    private async Task SetPlanAsync(string guid, string title)
    {
        await RunExclusiveAsync("план электропитания", async ct =>
        {
            StatusText = L.T("Установка плана: {0}…", title);
            var result = await _powerService.SetPowerPlanAsync(guid, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess
                ? L.T("План «{0}» установлен.", title)
                : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
            if (result.IsSuccess)
            {
                _logger.Info("POWER | plan | " + title);
                _history.Enqueue(new HistoryEvent(
                    DateTime.Now,
                    L.T("Питание"),
                    L.T("План электропитания: {0}", title),
                    HistoryEvent.StatusOk));
                await RefreshPlansAsync(ct).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    // Ресинк тумблера на пути отмены: чтение с CancellationToken.None, вторичные
    // исключения глотаются — не маскируем исходный OperationCanceledException.
    private async Task ResyncOnCancelAsync(Func<bool> read, Action<bool> apply)
    {
        try
        {
            apply(await TaskRunner.RunBlocking(read, CancellationToken.None).ConfigureAwait(true));
        }
        catch
        {
            // Лучший эффект: не перечиталось — оставляем тумблер как есть.
        }
    }

    // Тумблеры: цель — новое значение свойства (связка TwoWay обновляет его до команды).
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleHibernationAsync()
    {
        var enable = HibernationEnabled;
        await RunExclusiveAsync("гибернация", async ct =>
        {
            StatusText = L.T(enable ? "Включение гибернации…" : "Отключение гибернации…");
            try
            {
                var result = await _powerService.SetHibernationAsync(enable, ct).ConfigureAwait(true);
                // Читаем фактическое состояние — тумблер обязан показать правду даже при отказе.
                HibernationEnabled = await TaskRunner.RunBlocking(PowerService.IsHibernationEnabled, ct).ConfigureAwait(true);
                StatusText = result.IsSuccess
                    ? L.S(result.Message)
                    : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
            }
            catch (OperationCanceledException)
            {
                // Тумблер ресинхронизируется с фактическим состоянием, иначе он расходится с системой.
                await ResyncOnCancelAsync(PowerService.IsHibernationEnabled, value => HibernationEnabled = value).ConfigureAwait(true);
                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleFastBootAsync()
    {
        var enable = FastBootEnabled;
        await RunExclusiveAsync("быстрый запуск", async ct =>
        {
            StatusText = L.T(enable ? "Включение быстрого запуска…" : "Отключение быстрого запуска…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _powerService.SetFastBoot(!enable), ct).ConfigureAwait(true);
                FastBootEnabled = await TaskRunner.RunBlocking(() => !PowerService.IsFastBootDisabled(_registry), ct).ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    // Запись в реестр прошла; полное применение hybrid shutdown — после перезагрузки.
                    FastBootState = SystemSettingState.PendingReboot;
                    FastBootStateBadge = FormatStateBadge(SystemSettingState.PendingReboot);
                    StatusText = L.S(result.Message) + " " + L.T("Требуется перезагрузка для полного применения.");
                }
                else
                {
                    StatusText = L.T("Ошибка: {0}", result.Message);
                }
            }
            catch (OperationCanceledException)
            {
                await ResyncOnCancelAsync(
                    () => !PowerService.IsFastBootDisabled(_registry),
                    value => FastBootEnabled = value).ConfigureAwait(true);
                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleMemoryCompressionAsync()
    {
        var enable = MemoryCompressionEnabled;
        await RunExclusiveAsync("сжатие памяти", async ct =>
        {
            StatusText = L.T(enable ? "Включение сжатия памяти…" : "Отключение сжатия памяти…");
            try
            {
                var result = await _powerService.SetMemoryCompressionAsync(enable, ct).ConfigureAwait(true);
                var state = await _powerService.GetMemoryCompressionAsync(ct).ConfigureAwait(true);
                if (state.IsSuccess)
                {
                    MemoryCompressionEnabled = state.Value;
                    MemoryCompressionState = state.Value
                        ? SystemSettingState.Enabled
                        : SystemSettingState.Disabled;
                    MemoryCompressionStateBadge = string.Empty;
                }
                else
                {
                    // Не считаем ошибку чтения подтверждением OFF.
                    MemoryCompressionState = SystemSettingState.Unknown;
                    MemoryCompressionStateBadge = "?";
                }

                StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    var state = await _powerService.GetMemoryCompressionAsync(CancellationToken.None).ConfigureAwait(true);
                    if (state.IsSuccess)
                    {
                        MemoryCompressionEnabled = state.Value;
                        MemoryCompressionState = state.Value
                            ? SystemSettingState.Enabled
                            : SystemSettingState.Disabled;
                        MemoryCompressionStateBadge = string.Empty;
                    }
                    else
                    {
                        MemoryCompressionState = SystemSettingState.Unknown;
                        MemoryCompressionStateBadge = "?";
                    }
                }
                catch
                {
                    // Лучший эффект: не перечиталось — не маскируем исходную отмену.
                }

                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleSysMainAsync()
    {
        // Единая семантика с вкладкой «Службы»: тумблер = разрешён ли автозапуск (Startup Type),
        // а не «запустить/остановить сейчас». Не вызываем SetAutomaticAsync (он ещё и Start).
        var enable = SysMainEnabled;
        await RunExclusiveAsync("SysMain", async ct =>
        {
            StatusText = L.T(enable ? "Разрешение запуска SysMain…" : "Запрет запуска SysMain…");
            try
            {
                // disable: true → Startup=Disabled (+ stop); false → восстановить тип запуска без принудительного Start.
                var result = await _serviceManager.SetDisabledAsync("SysMain", disable: !enable, ct).ConfigureAwait(true);
                var actual = await TaskRunner.RunBlocking(PowerService.TryIsSysMainEnabled, ct).ConfigureAwait(true);
                if (actual is bool on)
                    SysMainEnabled = on;
                StatusText = result.IsSuccess
                    ? L.S("SysMain: " + result.Message)
                    : L.T("Ошибка: {0}", result.Message);
            }
            catch (OperationCanceledException)
            {
                await ResyncOnCancelAsync(
                    () => PowerService.TryIsSysMainEnabled() ?? SysMainEnabled,
                    value => SysMainEnabled = value).ConfigureAwait(true);
                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleShortNamesAsync()
    {
        var enable = ShortNamesEnabled;
        await RunExclusiveAsync("имена 8.3", async ct =>
        {
            StatusText = L.T(enable ? "Включение имён 8.3…" : "Отключение имён 8.3…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _powerService.Set8dot3NamesEnabled(enable), ct).ConfigureAwait(true);
                var shortInfo = PowerService.GetShortNamesInfo();
                if (shortInfo.GlobalMode is ShortNameGlobalMode.EnabledForAll or ShortNameGlobalMode.DisabledForAll)
                    ShortNamesEnabled = shortInfo.GlobalMode == ShortNameGlobalMode.EnabledForAll;
                ShortNamesState = result.IsSuccess
                    ? SystemSettingState.PendingReboot
                    : shortInfo.EffectiveState;
                ShortNamesStateBadge = FormatStateBadge(ShortNamesState);
                StatusText = result.IsSuccess
                    ? L.S(result.Message) + " " + L.T("Требуется перезагрузка для полного применения.")
                    : L.T("Ошибка: {0}", result.Message);
            }
            catch (OperationCanceledException)
            {
                await ResyncOnCancelAsync(PowerService.Is8dot3Enabled, value => ShortNamesEnabled = value).ConfigureAwait(true);
                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ToggleLastAccessAsync()
    {
        var enable = LastAccessEnabled;
        await RunExclusiveAsync("учёт времени доступа", async ct =>
        {
            StatusText = L.T(enable ? "Включение учёта времени доступа…" : "Отключение учёта времени доступа…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _powerService.SetLastAccessEnabled(enable), ct).ConfigureAwait(true);
                var infoResult = await _powerService.GetLastAccessInfoAsync(ct).ConfigureAwait(true);
                var info = infoResult.IsSuccess && infoResult.Value is not null
                    ? infoResult.Value
                    : PowerService.GetLastAccessInfo();
                if (info.State is SystemSettingState.Enabled or SystemSettingState.Disabled)
                    LastAccessEnabled = info.State == SystemSettingState.Enabled;
                LastAccessState = info.RequiresReboot ? SystemSettingState.PendingReboot : info.State;
                LastAccessStateBadge = FormatStateBadge(LastAccessState);
                StatusText = result.IsSuccess
                    ? L.S(result.Message) + (info.RequiresReboot
                        ? " " + L.T("Требуется перезагрузка для полного применения.")
                        : string.Empty)
                    : L.T("Ошибка: {0}", result.Message);
            }
            catch (OperationCanceledException)
            {
                await ResyncOnCancelAsync(PowerService.IsLastAccessEnabled, value => LastAccessEnabled = value).ConfigureAwait(true);
                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task TogglePrefetcherAsync()
    {
        var enable = PrefetcherEnabled;
        await RunExclusiveAsync("prefetcher", async ct =>
        {
            StatusText = L.T(enable ? "Включение prefetcher…" : "Отключение prefetcher…");
            try
            {
                var result = await TaskRunner.RunBlocking(() => _powerService.SetPrefetcherEnabled(enable), ct).ConfigureAwait(true);
                PrefetcherEnabled = await TaskRunner.RunBlocking(PowerService.IsPrefetcherEnabled, ct).ConfigureAwait(true);
                StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            }
            catch (OperationCanceledException)
            {
                await ResyncOnCancelAsync(PowerService.IsPrefetcherEnabled, value => PrefetcherEnabled = value).ConfigureAwait(true);
                throw;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task SetPageFileAsync(string? sizeMb)
    {
        if (sizeMb is null
            || !int.TryParse(sizeMb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            || size < 512)
        {
            StatusText = L.T("Размер файла подкачки не распознан.");
            return;
        }

        // Читаем фактическое состояние ДО диалога: multi-pagefile + crash dump.
        var current = await _powerService.GetPageFileStateAsync().ConfigureAwait(true);
        var volumesLine = "системный том";
        var multiNote = "";
        var crashNote = "";
        if (current.IsSuccess && current.Value is not null)
        {
            var pf = current.Value;
            if (pf.Entries.Count > 0)
            {
                volumesLine = string.Join(", ", pf.Entries.Select(e => e.Path));
                if (pf.Entries.Count > 1)
                {
                    multiNote = "\n\nНа системе несколько файлов подкачки. Размер будет применён ко ВСЕМ перечисленным томам.";
                }
            }

            if (pf.CrashDumpMayRequirePagefile)
            {
                crashNote = "\n\n" + (pf.CrashDumpSummary ?? "Дамп памяти")
                    + "\nУменьшение файла подкачки может повлиять на возможность создания дампа памяти после BSOD.";
            }
        }

        if (!_dialogs.ConfirmChange(new DestructiveChange(
                L.T("Файл подкачки"),
                CurrentState: L.T("Тома с файлом подкачки: {0}.", volumesLine),
                NewState: L.T("Фиксированный размер {0} МБ на всех перечисленных томах.", size),
                Consequences: L.T("Автоматическое управление Windows будет отключено. Изменения вступят в силу после перезагрузки.")
                    + multiNote.Replace("\n\n", " ") + crashNote.Replace("\n\n", " "),
                Rollback: L.T("Прежние настройки файла подкачки сохраняются в бэкап автоматически."),
                ConfirmText: L.T("Установить"))))
        {
            return;
        }

        await RunExclusiveAsync("файл подкачки", async ct =>
        {
            StatusText = L.T("Настройка файла подкачки ({0} МБ)…", size);
            var result = await _powerService.SetPageFileAsync(size, ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            if (result.IsSuccess)
            {
                var state = await _powerService.GetPageFileStateAsync(ct).ConfigureAwait(true);
                ApplyPageFileState(state);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task SetSystemManagedPageFileAsync()
    {
        if (!_dialogs.ConfirmChange(new DestructiveChange(
                L.T("Файл подкачки"),
                CurrentState: L.T("Размер файла подкачки задан вручную."),
                NewState: L.T("Управление размером передаётся Windows (System Managed)."),
                Consequences: L.T("Фиксированные размеры будут сняты. Изменение вступит в силу после перезагрузки."),
                Rollback: L.T("Прежние настройки файла подкачки сохраняются в бэкап автоматически."),
                ConfirmText: L.T("Управляется Windows"))))
        {
            return;
        }

        await RunExclusiveAsync("pagefile system managed", async ct =>
        {
            StatusText = L.T("Включение System Managed pagefile…");
            var result = await _powerService.SetSystemManagedPageFileAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка: {0}", result.Message);
            if (result.IsSuccess)
            {
                var state = await _powerService.GetPageFileStateAsync(ct).ConfigureAwait(true);
                ApplyPageFileState(state);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ClearCpuLimitsAsync()
    {
        if (!_dialogs.ConfirmChange(new DestructiveChange(
                "Ограничения CPU и ОЗУ",
                CurrentState: L.T("В BCD заданы параметры numproc/truncatememory (если они есть)."),
                NewState: L.T("Параметры numproc и truncatememory удалены из BCD."),
                Consequences: L.T("Windows больше не будет ограничивать число CPU и объём ОЗУ через эти параметры. Применяется после перезагрузки."),
                Rollback: L.T("Резервная копия BCD создаётся автоматически перед изменением."),
                ConfirmText: L.T("Убрать ограничения"))))
        {
            return;
        }

        await RunExclusiveAsync("bcdedit: снять ограничения", async ct =>
        {
            StatusText = L.T("Снятие ограничений numproc/truncatememory…");
            var result = await _powerService.ClearCpuMemoryLimitsAsync(ct).ConfigureAwait(true);
            StatusText = result.IsSuccess ? L.S(result.Message) : L.T("Ошибка (код {0}): {1}", result.Code, result.Message);
            if (result.IsSuccess)
            {
                var summary = await _powerService.GetCpuLimitsSummaryAsync(ct).ConfigureAwait(true);
                CpuLimitsText = summary.IsSuccess ? summary.Value ?? string.Empty : CpuLimitsText;
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _logger.Warn("CANCEL | power operation");
        _operationCts?.Cancel();
        StatusText = L.T("Отмена операции…");
    }

    // Пересчёт подсветки активной схемы: читается фактический GUID из powercfg
    // (после SetPlanAsync тоже — вызывается из её хвоста).
    private async Task RefreshPlansAsync(CancellationToken ct)
    {
        var active = await _powerService.GetActivePlanGuidAsync(ct).ConfigureAwait(true);
        ActivePlanGuid = active.IsSuccess ? active.Value : null;
    }

    private void ApplyPageFileState(Result<PageFileInfo> state)
    {
        if (!state.IsSuccess || state.Value is null)
        {
            PageFileSummaryText = L.T("Файл подкачки: состояние не удалось определить.");
            ActivePageFileKey = null;
            return;
        }

        var pf = state.Value;
        if (pf.SystemManaged)
        {
            ActivePageFileKey = "system";
            PageFileSummaryText = L.T("Режим: Управляется Windows");
            if (pf.Entries.Count > 0)
            {
                PageFileSummaryText += " | " + string.Join("; ", pf.Entries.Select(e => e.Path));
            }
        }
        else
        {
            // Подсветка пресета: одинаковый initial==maximum на всех, совпавший с кнопкой.
            string? key = null;
            if (pf.Entries.Count > 0
                && pf.Entries.All(e => e.InitialSizeMb == e.MaximumSizeMb && e.InitialSizeMb is not null))
            {
                var size = pf.Entries[0].InitialSizeMb!.Value;
                if (pf.Entries.All(e => e.InitialSizeMb == size)
                    && size is 2048 or 4096 or 6144 or 8192)
                {
                    key = size.ToString(CultureInfo.InvariantCulture);
                }
            }

            ActivePageFileKey = key;
            var lines = pf.Entries.Select(e =>
                e.InitialSizeMb is null
                    ? e.Path
                    : $"{e.Path}: {e.InitialSizeMb}–{e.MaximumSizeMb} МБ");
            PageFileSummaryText = L.T("Режим: Заданный вручную")
                + (pf.Entries.Count > 0 ? " | " + string.Join("; ", lines) : "");
        }

        if (!string.IsNullOrEmpty(pf.CrashDumpSummary))
        {
            PageFileSummaryText += " | " + pf.CrashDumpSummary;
        }
    }

    // Отмена фоновой операции при уходе с раздела (вызывается MainViewModel).

    public void CancelOngoing() => _operationCts?.Cancel();


    public void Dispose()
    {
        L.LanguageChanged -= RefreshActionTexts;
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
            _logger.Error("POWER | " + title + " | " + exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
