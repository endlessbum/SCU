using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Services;
using SCU.Services.Dashboard;
using SCU.ViewModels.Sections;
using SCU.Views.Controls;

namespace SCU.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;

    public MainViewModel(Logger logger, SCURunner scuRunner)
    {
        _logger = logger;
        language = L.Current;

        Sections =
        [
            new SectionItem(0, "S_Section00_Title", "S_Section00_Desc", "\uE9D9", "S_Group_Overview"),
            new SectionItem(20, "S_Section20_Title", "S_Section20_Desc", "\uE9D2", "S_Group_Overview"),
            new SectionItem(1, "S_Section01_Title", "S_Section01_Desc", "\uE946", "S_Group_Overview"),
            new SectionItem(3, "S_Section03_Title", "S_Section03_Desc", "\uE74D", "S_Group_Cleanup"),
            new SectionItem(4, "S_Section04_Title", "S_Section04_Desc", "\uE711", "S_Group_Cleanup"),
            new SectionItem(12, "S_Section12_Title", "S_Section12_Desc", "\uE721", "S_Group_Cleanup"),
            new SectionItem(2, "S_Section02_Title", "S_Section02_Desc", "\uE719", "S_Group_Cleanup"),
            new SectionItem(5, "S_Section05_Title", "S_Section05_Desc", "\uE72E", "S_Group_Privacy"),
            new SectionItem(13, "S_Section13_Title", "S_Section13_Desc", "\uE72E", "S_Group_Privacy"),
            new SectionItem(21, "S_Section21_Title", "S_Section21_Desc", "\uEA18", "S_Group_Privacy"),
            new SectionItem(6, "S_Section06_Title", "S_Section06_Desc", "\uE713", "S_Group_System"),
            new SectionItem(19, "S_Section19_Title", "S_Section19_Desc", "\uE71D", "S_Group_System"),
            new SectionItem(7, "S_Section07_Title", "S_Section07_Desc", "\uE768", "S_Group_System"),
            new SectionItem(15, "S_Section15_Title", "S_Section15_Desc", "\uE823", "S_Group_System"),
            new SectionItem(16, "S_Section16_Title", "S_Section16_Desc", "\uE895", "S_Group_System"),
            new SectionItem(8, "S_Section08_Title", "S_Section08_Desc", "\uE7E8", "S_Group_Tuning"),
            new SectionItem(9, "S_Section09_Title", "S_Section09_Desc", "\uE701", "S_Group_Tuning"),
            new SectionItem(10, "S_Section10_Title", "S_Section10_Desc", "\uE8B7", "S_Group_Tuning"),
            new SectionItem(11, "S_Section11_Title", "S_Section11_Desc", "\uE7FC", "S_Group_Tuning"),
            new SectionItem(18, "S_Section18_Title", "S_Section18_Desc", "\uE81C", "S_Group_App"),
            new SectionItem(17, "S_Section17_Title", "S_Section17_Desc", "\uE713", "S_Group_App"),
        ];

        IsAdmin = Elevation.IsAdmin();
        IsSCUAvailable = scuRunner.IsAvailable;
        // Статусы вычисляются через L.T — пересчитываются при смене языка
        // (по образцу SectionItem: подписка живёт столько же, сколько приложение).
        L.LanguageChanged += RefreshStatusTexts;
        // Язык применяется до создания окна (событие уже прошло) — считаем тексты сами.
        // Через L.T: при старте с английским языком значения должны быть переведены.
        RefreshStatusTexts();
        LogFilePath = _logger.FilePath;

        ThemeManager.SetAccent(ThemeManager.LoadAccentMode());
        themeMode = ThemeManager.LoadThemeMode();
        accentMode = ThemeManager.Accent;
        transparencyMode = ThemeManager.LoadTransparency();
        ThemeManager.SetTransparency(transparencyMode);

        // Тумблер «UAC» в настройках: глобальная политика подтверждений читается
        // до создания разделов — первые операции уже подчиняются ей.
        SecurityPrompts.Enabled = ThemeManager.LoadUacConfirmations();
        
        var dialogs = new ConfirmDialogService();
        _dialogs = dialogs;
        // История операций Dashboard: один экземпляр на приложение, передаётся только
        // тем VM, где есть хук записи (минимальный путь вместо static-синглтона).
        var history = new HistoryStore(_logger);
        Info = new InfoViewModel(_logger, new SystemInfoService());
        Components = new ComponentsViewModel(_logger, scuRunner, dialogs);
        Cleanup = new CleanupViewModel(
            _logger,
            scuRunner,
            new FileCleanupService(_logger),
            new ServiceManager(),
            dialogs,
            history);
        Startup = new StartupViewModel(_logger, scuRunner, dialogs, history);
        Tasks = new TasksViewModel(_logger, scuRunner, new TaskManager(scuRunner), dialogs, history);
        Services = new ServicesViewModel(_logger, scuRunner, new ServiceManager(), history);
        Privacy = new PrivacyViewModel(_logger, scuRunner, dialogs);
        var longRunner = new LongProcessRunner(_logger);
        Power = new PowerViewModel(_logger, longRunner, dialogs, history);
        Network = new NetworkViewModel(_logger, dialogs, history);
        Ui = new UIViewModel(_logger, dialogs);
        Input = new InputViewModel(_logger, dialogs);
        Bloat = new BloatViewModel(_logger, scuRunner, dialogs, history);
        Maintenance = new MaintenanceViewModel(_logger, longRunner, dialogs, history);
        Security = new SecurityViewModel(_logger, dialogs);
        Update = new UpdateViewModel(_logger, scuRunner, dialogs, history);
        // Раздел 21 «Сканер»: ScannerRunner по образцу SCURunner, история
        // сканов пишется в тот же HistoryStore, что и операции других разделов.
        Scanner = new ScannerViewModel(_logger, new ScannerRunner(_logger), history);

        // Раздел 0 «Состояние ПК»: большой выключатель пакетного применения.
        // Ссылки на VM разделов передаются после их создания — реестр утилит
        // собирается из готовых команд и тумблеров.
        var systemState = new SystemStateService(_logger, scuRunner);
        Dashboard = new DashboardViewModel(
            _logger,
            history,
            new BatchStateStore(_logger),
            Cleanup,
            Privacy,
            Services,
            Tasks,
            Power,
            Network,
            Ui,
            Input,
            Maintenance,
            Security,
            Update);

        // Подсказка поиска на «Главной» открывает вкладку раздела настройки и
        // просит окно один раз подсветить строку утилиты, по которой тапнули.
        Dashboard.NavigateToSectionRequested += (number, title) =>
        {
            SelectSectionByNumber(number);
            SectionHighlightRequested?.Invoke(number, title);
        };

        // Раздел 18 «История»: тот же экземпляр HistoryStore, что пишут все разделы.
        HistorySection = new HistoryViewModel(_logger, history);

        // Раздел 20 «Бэнчмарк»: расчёт по SystemStateService, журнал — benchmark.json.
        Benchmark = new BenchmarkViewModel(_logger, systemState, new SnapshotStore(_logger), new BenchmarkStore(_logger), history);
        // Пакет «Состояния ПК» меняет систему массово: после него — один повторный бэнчмарк.
        Dashboard.BatchCompleted += () => TaskRunner.RunAndForget(
            Benchmark.RunBenchmarkCommand.ExecuteAsync(null), _logger, "benchmark after batch");
        // Строки потенциала и метрик ведут в разделы, которые могут их улучшить.
        // Конкретного элемента подсветки нет — окно подсвечивает пункт меню.
        Benchmark.NavigateToSectionRequested += number =>
        {
            SelectSectionByNumber(number);
            SectionHighlightRequested?.Invoke(number, null);
        };

        // Раздел 19 «Приложения»: чтение Uninstall-реестра и запуск деинсталляторов.
        Apps = new AppsViewModel(_logger, dialogs);

        foreach (var line in _logger.Snapshot())
        {
            LogLines.Add(line);
        }

        _logger.LineWritten += OnLogLineWritten;
        CurrentSection = Sections[0];
    }

    public ObservableCollection<SectionItem> Sections { get; }

    // Подсветка элемента после перехода по ссылке (слушает MainWindow).
    // title — заголовок строки в открывшемся разделе (из подсказки поиска);
    // null — конкретного элемента нет, окно подсвечивает пункт меню.
    public event Action<int, string?>? SectionHighlightRequested;

    // Пересчёт статусов из конструктора: логика та же, что была при однократном вычислении.
    private void RefreshStatusTexts()
    {
        AdminStatusText = L.T(IsAdmin ? "есть" : "нет — изменяющие операции недоступны");
        ScuStatusText = L.T(IsSCUAvailable ? "да" : "нет");
    }

    // Стартовая инициализация: только «Главная» и «Бэнчмарк» (п. 13 аудита).
    // Остальные разделы инициализируются лениво при первом открытии
    // (EnsureSectionInitialized) — заставка исчезает значительно быстрее.
    public async Task InitializeAsync()
    {
        await RunSectionInitAsync("Dashboard", () => Dashboard.InitializeAsync()).ConfigureAwait(true);
        await RunSectionInitAsync("Benchmark", () => Benchmark.InitializeAsync()).ConfigureAwait(true);

        // «К применению» на «Главной»: статусы Dashboard уже прочитаны.
        Dashboard.RecomputePending();
    }

    private Dictionary<int, Func<Task>>? _sectionInits;
    private readonly HashSet<int> _initializedSections = [];

    // Ленивая инициализация раздела при первом открытии: тот же код, что раньше
    // выполнялся целиком на заставке. Повторное открытие — без повторного refresh.
    private void EnsureSectionInitialized(int? number)
    {
        if (number is null || !_initializedSections.Add(number.Value))
        {
            return;
        }

        _sectionInits ??= new Dictionary<int, Func<Task>>
        {
            [1] = () => ExecuteRefreshAsync(Info.RefreshCommand),
            [2] = () => ExecuteRefreshAsync(Components.RefreshCommand),
            [4] = () => Bloat.InitializeAsync()
                .ContinueWith(_ => ExecuteRefreshAsync(Bloat.RefreshCommand), TaskScheduler.FromCurrentSynchronizationContext()),
            [5] = () => ExecuteRefreshAsync(Privacy.RefreshCommand),
            [6] = () => Services.InitializeAsync()
                .ContinueWith(_ => ExecuteRefreshAsync(Services.RefreshCommand), TaskScheduler.FromCurrentSynchronizationContext()),
            [7] = () => Startup.InitializeAsync()
                .ContinueWith(_ => ExecuteRefreshAsync(Startup.RefreshCommand), TaskScheduler.FromCurrentSynchronizationContext()),
            [8] = () => ExecuteRefreshAsync(Power.RefreshCommand),
            [9] = () => ExecuteRefreshAsync(Network.RefreshCommand),
            [10] = () => ExecuteRefreshAsync(Ui.RefreshCommand),
            [11] = () => ExecuteRefreshAsync(Input.RefreshCommand),
            [12] = () => ExecuteRefreshAsync(Maintenance.RefreshCommand),
            [13] = () => ExecuteRefreshAsync(Security.RefreshCommand),
            [15] = () => Tasks.InitializeAsync()
                .ContinueWith(_ => ExecuteRefreshAsync(Tasks.RefreshCommand), TaskScheduler.FromCurrentSynchronizationContext()),
            [16] = () => ExecuteRefreshAsync(Update.RefreshCommand),
            [19] = () => ExecuteRefreshAsync(Apps.RefreshCommand),
        };

        if (!_sectionInits.TryGetValue(number.Value, out var init))
        {
            return;
        }

        RunSectionInitAsync("lazy-" + number.Value, init);
    }

    // Инициализация/refresh одного раздела: ошибка логируется, остальные продолжают работу.
    private async Task RunSectionInitAsync(string section, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Error("INIT | " + section + " load failed | " + exception);
        }
    }

    private static Task ExecuteRefreshAsync(IAsyncRelayCommand command) =>
        command.CanExecute(null) ? command.ExecuteAsync(null) : Task.CompletedTask;

    public ObservableCollection<string> LogLines { get; } = new();

    public InfoViewModel Info { get; }

    public DashboardViewModel Dashboard { get; }

    public HistoryViewModel HistorySection { get; }

    public BenchmarkViewModel Benchmark { get; }

    public AppsViewModel Apps { get; }

    public ComponentsViewModel Components { get; }

    public CleanupViewModel Cleanup { get; }

    public StartupViewModel Startup { get; }

    public TasksViewModel Tasks { get; }

    public ServicesViewModel Services { get; }

    public PrivacyViewModel Privacy { get; }

    public PowerViewModel Power { get; }

    public NetworkViewModel Network { get; }

    public UIViewModel Ui { get; }

    public InputViewModel Input { get; }

    public BloatViewModel Bloat { get; }

    public MaintenanceViewModel Maintenance { get; }

    public SecurityViewModel Security { get; }

    public UpdateViewModel Update { get; }

    public ScannerViewModel Scanner { get; }

    [ObservableProperty]
    private SectionItem? currentSection;

    [ObservableProperty]
    private bool isAdmin;

    [ObservableProperty]
    private string adminStatusText = string.Empty;

    [ObservableProperty]
    private bool isSCUAvailable;

    [ObservableProperty]
    private string scuStatusText = string.Empty;

    [ObservableProperty]
    private string logFilePath = string.Empty;

    [ObservableProperty]
    private AppTheme themeMode;

    partial void OnThemeModeChanged(AppTheme value)
    {
        ThemeManager.ApplyTheme(value);
        ThemeManager.SaveSettings(value, AccentMode, Language, ShowLog, UacConfirmations);
        OnPropertyChanged(nameof(ThemeModeIndex));
    }

    // Индекс для ComboBox «Настроек»: 0 = «Как в системе», 1 = «Светлая», 2 = «Тёмная».
    public int ThemeModeIndex
    {
        get => (int)ThemeMode;
        set => ThemeMode = (AppTheme)value;
    }

    [ObservableProperty]
    private AppAccent accentMode;

    partial void OnAccentModeChanged(AppAccent value)
    {
        ThemeManager.SetAccent(value);
        ThemeManager.ApplyTheme(ThemeMode);
        ThemeManager.SaveSettings(ThemeMode, value, Language, ShowLog, UacConfirmations);
        OnPropertyChanged(nameof(AccentModeIndex));
    }

    // Индекс для ComboBox акцента: 0 = синий, 1 = бирюзовый, 2 = фиолетовый,
    // 3 = зелёный, 4 = оранжевый (пункт «Как в системе» в UI не показывается;
    // системный акцент маппится на синий).
    public int AccentModeIndex
    {
        get => AccentMode == AppAccent.System ? 0 : (int)AccentMode - 1;
        set => AccentMode = (AppAccent)(value + 1);
    }

    // Режим прозрачности поверхностей (Liquid Glass): единый переключатель всей
    // дизайн-системы. Применяется ThemeManager'ом централизованно и сохраняется
    // в settings.json — работает вместе с Light/Dark темой.
    [ObservableProperty]
    private AppTransparency transparencyMode;

    partial void OnTransparencyModeChanged(AppTransparency value)
    {
        ThemeManager.SetTransparency(value);
        OnPropertyChanged(nameof(TransparencyIndex));
    }

    // Индекс для сегментированного контроля «Настроек»: 0 = «Прозрачный», 1 = «Матовый».
    public int TransparencyIndex
    {
        get => (int)TransparencyMode;
        set => TransparencyMode = (AppTransparency)value;
    }

    [ObservableProperty]
    private AppLanguage language;

    partial void OnLanguageChanged(AppLanguage value)
    {
        L.SetLanguage(value);
        ThemeManager.SaveSettings(ThemeMode, AccentMode, value, ShowLog, UacConfirmations);
        OnPropertyChanged(nameof(LanguageIndex));
    }

    // Индекс для ComboBox языка: 0 = русский, 1 = English.
    public int LanguageIndex
    {
        get => (int)Language;
        set => Language = (AppLanguage)value;
    }

    [ObservableProperty]
    private bool showLog = ThemeManager.LoadShowLog();

    partial void OnShowLogChanged(bool value)
    {
        ThemeManager.SaveSettings(ThemeMode, AccentMode, Language, value, UacConfirmations);
    }

    // Тумблер «UAC» в настройках: окно подтверждения рисковых действий.
    // Включено по умолчанию; при выключении все подтверждения в приложении
    // пропускаются (SecurityPrompts.Enabled), а перед отключением показывается
    // предупреждающее уведомление UAC.
    [ObservableProperty]
    private bool uacConfirmations = ThemeManager.LoadUacConfirmations();

    partial void OnUacConfirmationsChanged(bool value)
    {
        if (!value && !_dialogs.Ask(
                L.T("UAC"),
                L.T("Отключить подтверждения рисковых действий?\nОперации будут выполняться сразу, без окна подтверждения."),
                L.T("Отключить")))
        {
            // Отказ в предупреждении — переключатель возвращается во включённое положение.
            UacConfirmations = true;
            return;
        }

        SecurityPrompts.Enabled = value;
        ThemeManager.SaveSettings(ThemeMode, AccentMode, Language, ShowLog, value);
        if (value)
        {
            _logger.Info("SETTINGS | UAC confirmations enabled");
        }
        else
        {
            _logger.Warn("SETTINGS | UAC confirmations disabled — risky actions run without confirmation");
        }
    }

    public bool IsDashboardSection => CurrentSection?.Number == 0;

    // ===================== «О приложении» (вкладка «Настройки») =====================

    // Версия сборки — из версии входной сборки; вес — размер файла запускаемого exe.
    public string AboutVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "—";

    public string AboutSize
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                return path is null
                    ? "—"
                    : L.T("{0:0.0} МБ", new FileInfo(path).Length / 1024.0 / 1024.0);
            }
            catch
            {
                return "—";
            }
        }
    }

    public string AboutSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SCU", "settings.json");

    public string AboutStatePath => SCU.Services.Dashboard.DashboardStatePaths.Directory;

    public string AboutBackupPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SCU", "backup");

    public string AboutLogPath => _logger.FilePath;


    // Все номера разделов имеют реализацию (п. 13: navigation host);
    // заглушка остаётся на случай раздела без View.
    public bool IsPlaceholderSection => CurrentSection is null;

    // Переход в раздел по номеру (из карточек и рекомендаций Dashboard). Тот же
    // механизм, что клик по сайдбару: просто присваивание CurrentSection.
    public void SelectSectionByNumber(int number)
    {
        var target = Sections.FirstOrDefault(section => section.Number == number);
        if (target is null || ReferenceEquals(target, CurrentSection))
        {
            return;
        }

        CurrentSection = target;
    }

    // П. 13 аудита: окно подписывается и подменяет содержимое navigation host.
    public event Action<int?>? CurrentSectionChanged;

    partial void OnCurrentSectionChanged(SectionItem? value)
    {
        CurrentSectionChanged?.Invoke(value?.Number);
        EnsureSectionInitialized(value?.Number);

        // Данные всех вкладок читаются один раз при старте (InitializeAsync). Открытие
        // вкладки больше не запускает refresh. Фоновые операции (установка, загрузка,
        // удаление, сканирование и т.д.) НЕ отменяются при уходе с вкладки: процесс
        // продолжается, прогресс виден по возвращении; отменить можно кнопкой
        // «Отмена» в самом разделе.

        // Открытие вкладки «Бэнчмарк»: пометка «индекс устарел», если после
        // последнего запуска в истории появились операции.
        if (value?.Number == 20)
        {
            TaskRunner.RunAndForget(Benchmark.ActivateAsync(), _logger, "benchmark activate");
        }

        OnPropertyChanged(nameof(IsDashboardSection));
        OnPropertyChanged(nameof(IsPlaceholderSection));

        // Раздел «История» перечитывает хранилище при каждом открытии: события пишут
        // все разделы, кэшировать их на старте бессмысленно.
        if (value?.Number == 18)
        {
            TaskRunner.RunAndForget(HistorySection.LoadAsync(), _logger, "history load");
        }

        // Раздел «Сканер»: карантин перечитывается при каждом открытии.
        if (value?.Number == 21)
        {
            Scanner.RefreshQuarantineList();
        }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        _logger.Info("ELEVATION | requested");
        var result = Elevation.RelaunchElevated();
        if (result.IsSuccess)
        {
            _logger.Info("ELEVATION | " + result.Message);
            Application.Current?.Shutdown();
            return;
        }

        _logger.Error($"ELEVATION | rc={result.Code} | {result.Message}");
        MessageBox.Show(
            result.Message,
            L.T("Права администратора"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private const int MaxLogLines = 5000;

    private void OnLogLineWritten(string line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        void Append()
        {
            LogLines.Add(line);
            // Длинные операции (DISM/SFC) дают тысячи строк — ограничиваем память журнала.
            while (LogLines.Count > MaxLogLines)
            {
                LogLines.RemoveAt(0);
            }
        }

        if (dispatcher.CheckAccess())
        {
            Append();
            return;
        }

        // BeginInvoke: Invoke блокировал бы поток чтения stdout процесса на каждый вывод
        // и фризил UI на потоке строк DISM/SFC.
        dispatcher.BeginInvoke(Append);
    }

    public void LogInitializationError(Exception exception)
    {
        _logger.Error("INIT | deferred state load failed | " + exception);
    }

    public void Dispose()
    {
        _logger.LineWritten -= OnLogLineWritten;
        Info.Dispose();
        Components.Dispose();
        Cleanup.Dispose();
        Privacy.Dispose();
        Power.Dispose();
        Network.Dispose();
        Ui.Dispose();
        Input.Dispose();
        Bloat.Dispose();
        Maintenance.Dispose();
        Security.Dispose();
        Startup.Dispose();
        Tasks.Dispose();
        Services.Dispose();
        Update.Dispose();
        Apps.Dispose();
        Scanner.Dispose();
    }
}