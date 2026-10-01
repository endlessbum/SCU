using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.AppCore.AI;
using SCU.AppCore.Help;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.Models.AI;
using SCU.ViewModels.Sections;
using SCU.Views.Controls;

namespace SCU.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable, IScuAiEnvironment
{
    private readonly Logger _logger;

    // AI-помощник: один экземпляр на приложение (п. 29 ТЗ), навигация — через
    // этот же MainViewModel (IScuAiEnvironment, п. 30 ТЗ).
    private ScuAiAssistant? _aiAssistant;

    // Доступ для окон-редакторов (MenuEditorWindow) и VM пользовательских вкладок.
    internal Logger Log => _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly IFilePickerService _filePicker = new FilePickerService();
    private readonly IShellOpenService _shell = new ShellOpenService();

    public MainViewModel(Logger logger, SCURunner scuRunner)
    {
        _logger = logger;
        language = L.Current;

        Sections =
        [
            // «Главная» — без группы: в сайдбаре стоит над заголовком «Обзор».
            new SectionItem(0, "S_Section00_Title", "S_Section00_Desc", "\uE9D9"),
            new SectionItem(20, "S_Section20_Title", "S_Section20_Desc", "\uE9D2", "S_Group_Overview"),
            new SectionItem(1, "S_Section01_Title", "S_Section01_Desc", "\uE946", "S_Group_Overview"),
            new SectionItem(3, "S_Section03_Title", "S_Section03_Desc", "\uE74D", "S_Group_Cleanup"),
            new SectionItem(4, "S_Section04_Title", "S_Section04_Desc", "\uE711", "S_Group_Cleanup"),
            new SectionItem(12, "S_Section12_Title", "S_Section12_Desc", "\uE721", "S_Group_Cleanup"),
            new SectionItem(2, "S_Section02_Title", "S_Section02_Desc", "\uE719", "S_Group_Cleanup"),
            // Раздел 23 «Устранение неполадок»: read-only диагностика + подтверждаемые исправления.
            // Технический номер — 23; предыдущие разделы не перенумеровываются.
            new SectionItem(23, "S_Section23_Title", "S_Section23_Desc", "\uE7BA", "S_Group_Cleanup"),
            new SectionItem(5, "S_Section05_Title", "S_Section05_Desc", "\uE72E", "S_Group_Privacy"),
            new SectionItem(13, "S_Section13_Title", "S_Section13_Desc", "\uE72E", "S_Group_Privacy"),
            new SectionItem(21, "S_Section21_Title", "S_Section21_Desc", "\uEA18", "S_Group_Privacy"),
            new SectionItem(6, "S_Section06_Title", "S_Section06_Desc", "\uE713", "S_Group_System"),
            new SectionItem(19, "S_Section19_Title", "S_Section19_Desc", "\uE71D", "S_Group_System"),
            new SectionItem(7, "S_Section07_Title", "S_Section07_Desc", "\uE768", "S_Group_System"),
            new SectionItem(15, "S_Section15_Title", "S_Section15_Desc", "\uE823", "S_Group_System"),
            new SectionItem(16, "S_Section16_Title", "S_Section16_Desc", "\uE895", "S_Group_System"),
            // Раздел 25 «Драйверы»: инвентарь + обновления через Windows Update API
            // (25 — следующий свободный технический номер).
            new SectionItem(25, "S_Section25_Title", "S_Section25_Desc", "\uE965", "S_Group_System"),
            new SectionItem(8, "S_Section08_Title", "S_Section08_Desc", "\uE7E8", "S_Group_Tuning"),
            new SectionItem(9, "S_Section09_Title", "S_Section09_Desc", "\uE701", "S_Group_Tuning"),
            new SectionItem(10, "S_Section10_Title", "S_Section10_Desc", "\uE8B7", "S_Group_Tuning"),
            new SectionItem(11, "S_Section11_Title", "S_Section11_Desc", "\uE7FC", "S_Group_Tuning"),
            new SectionItem(22, "S_Section22_Title", "S_Section22_Desc", "\uE774", "S_Group_App"),
            // Раздел 24 «DeepSeek»: чат с ассистентом по API (24 — следующий
            // свободный технический номер; в меню стоит сразу после «Браузера»).
            new SectionItem(24, "S_Section24_Title", "S_Section24_Desc", "\uE99A", "S_Group_App"),
            new SectionItem(18, "S_Section18_Title", "S_Section18_Desc", "\uE81C", "S_Group_App"),
            new SectionItem(17, "S_Section17_Title", "S_Section17_Desc", "\uE713", "S_Group_App"),
        ];

        // Полный список встроенных разделов: редактор меню строит из него дерево,
        // чтобы скрытые разделы оставались доступными для возврата.
        _builtinSections = Sections.ToList();

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
        iconAccentMode = ThemeManager.LoadIconAccent();
        ThemeManager.IconAccent = iconAccentMode;
        // Смена темы меняет оттенок пресетов — обновляем квадраты выбора цвета.
        ThemeManager.ThemeApplied += _ => RefreshAccentChoices();
        transparencyMode = ThemeManager.LoadTransparency();
        ThemeManager.SetTransparency(transparencyMode);

        // Тумблер «UAC» в настройках: глобальная политика подтверждений читается
        // до создания разделов — первые операции уже подчиняются ей.
        SecurityPrompts.Enabled = ThemeManager.LoadUacConfirmations();
        // Глобальные горячие клавиши (Ctrl+Alt+S): состояние для карточки настроек.
        globalHotkeysEnabled = ThemeManager.LoadGlobalHotkeys();
        ThemeManager.GlobalHotkeysEnabled = globalHotkeysEnabled;
        L.LanguageChanged += RefreshHotkeyTexts;
        // Кастомизация меню: загрузка до создания окна (Sections уже построены,
        // применение — после создания всех VM в ApplyMenu).
        _menuStore = new MenuCustomizationStore(logger);
        _menu = _menuStore.Load();
        _userScriptStore = new UserScriptStore(logger);
        ReloadUserScripts();
        
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
        Services = new ServicesViewModel(_logger, scuRunner, new ServiceManager(), history, dialogs);
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
        // Раздел 25 «Драйверы»: инвентарь WMI + Windows Update API (COM),
        // история — тот же HistoryStore.
        Drivers = new DriversViewModel(_logger, history);
        // Раздел 23 «Устранение неполадок»: движок создаётся внутри VM вместе
        // с probes; исправления идут через тот же LongProcessRunner и HistoryStore.
        Troubleshooting = new TroubleshootingViewModel(_logger, dialogs, history, longRunner);
        // Раздел 21 «Сканер»: ScannerRunner по образцу SCURunner, история
        // сканов пишется в тот же HistoryStore, что и операции других разделов.
        Scanner = new ScannerViewModel(_logger, new ScannerRunner(_logger), history);

        // Раздел 22 «Браузер»: полностью ленивый — ни WebView2, ни окружение не
        // создаются ни на старте SCU, ни при открытии раздела; только «Запустить».
        Browser = new BrowserViewModel(
            _logger,
            dialogs,
            new BrowserService(_logger),
            new BrowserSettingsService(_logger),
            new BrowserHistoryService(_logger),
            new BrowserBookmarkService(_logger),
            new BrowserDownloadsService(_logger));

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

        // Раздел 23 «Устранение неполадок»: кнопки «Открыть …» ведут в существующие
        // разделы (очистка, обновления, сеть, службы и т.д.) — без дублирования.
        Troubleshooting.NavigateToSectionRequested += number => SelectSectionByNumber(number);

        // Раздел 19 «Приложения»: чтение Uninstall-реестра и запуск деинсталляторов.
        Apps = new AppsViewModel(_logger, dialogs);

        // Подписка на IsBusy отслеживаемых разделов — ПОСЛЕ создания всех VM
        // (Apps создаётся позже остальных; словарь ленивый, при раннем доступе
        // получился бы NullReferenceException на старте).
        foreach (var trackedViewModel in BusyTrackedSections.Values)
        {
            trackedViewModel.PropertyChanged += OnTrackedSectionPropertyChanged;
        }

        // Кастомизация меню: применение к Sections + настройка поиска и утилит.
        Dashboard.SectionTitleResolver = number =>
            Sections.FirstOrDefault(section => section.Number == number)?.Title ?? string.Empty;
        ApplyMenu();

        // Раздел 24 «DeepSeek»: ключ API и история чатов — в %AppData%\SCU\deepseek.
        // Создаётся до AI-слоя: контекст ассистента читает активное подключение
        // (провайдер/модель — без ключа) в момент хода, а не при сборке графа.
        var deepSeekClient = new DeepSeekClient(_logger);
        DeepSeek = new DeepSeekViewModel(
            _logger,
            dialogs,
            deepSeekClient,
            new DeepSeekSettingsService(_logger),
            new DeepSeekChatsService(_logger));

        // ===================== AI-слой (п. 29 ТЗ) =====================
        // Один экземпляр каждого сервиса на приложение: tools получают их через
        // ScuAiToolDeps, вторых экземпляров нет. Источник знаний — внутренняя
        // справка, построенная из тех же разделов/утилит/переключателей, что
        // видит пользователь; реестр capabilities — над Dashboard.Utilities.
        var helpService = new ScuHelpService(BuildHelpIndex);
        var plans = new ScuAiActionPlanStore(_logger);
        var capabilities = new ScuAiCapabilityRegistry(_logger, Dashboard, Ui, Input, Privacy);
        var toolDeps = new ScuAiToolDeps
        {
            Logger = _logger,
            Help = helpService,
            Plans = plans,
            Capabilities = capabilities,
            Environment = this,
            // Опасные AI-операции — через существующий диалог подтверждения
            // SCU (DestructiveChange), отдельный от карточки в чате.
            Dialogs = dialogs,
            State = new ScuAiStateSources
            {
                Info = Info,
                Power = Power,
                Network = Network,
                Privacy = Privacy,
                Ui = Ui,
                Input = Input,
                Services = Services,
                Startup = Startup,
                Update = Update,
                Maintenance = Maintenance,
                Troubleshooting = Troubleshooting,
            },
        };
        var planExecutor = new ScuAiPlanExecutor(toolDeps);
        var toolRegistry = new ScuAiToolRegistry(_logger, CreateAiTools(toolDeps, planExecutor));
        var contextBuilder = new ScuAiContextBuilder(this, DeepSeek);
        _aiAssistant = new ScuAiAssistant(
            _logger, deepSeekClient, toolRegistry, contextBuilder, planExecutor, this, helpService);
        DeepSeek.AttachAssistant(_aiAssistant);

        foreach (var line in _logger.Snapshot())
        {
            LogLines.Add(line);
        }

        _logger.LineWritten += OnLogLineWritten;
        CurrentSection = Sections[0];
    }

    public ObservableCollection<SectionItem> Sections { get; }

    private IReadOnlyList<SectionItem> _builtinSections = [];

    // Все встроенные разделы, включая скрытые через «Редактирование меню».
    public IReadOnlyList<SectionItem> BuiltinSections => _builtinSections;

    // Подсветка элемента после перехода по ссылке (слушает MainWindow).
    // title — заголовок строки в открывшемся разделе (из подсказки поиска);
    // null — конкретного элемента нет, окно подсвечивает пункт меню.
    public event Action<int, string?>? SectionHighlightRequested;

    // Пересчёт статусов из конструктора: логика та же, что была при однократном вычислении.
    private void RefreshHotkeyTexts()
    {
        OnPropertyChanged(nameof(GlobalHotkeysLabel));
        OnPropertyChanged(nameof(GlobalHotkeysDesc));
        OnPropertyChanged(nameof(SectionHotkeysDesc));
    }

    public string GlobalHotkeysLabel => L.T("Глобальные горячие клавиши");
    public string GlobalHotkeysDesc => L.T("Ctrl+Alt+S — показать или скрыть окно SCU из любого приложения");
    public string SectionHotkeysDesc => L.T("Ctrl+1…Ctrl+9 — быстрый переход к разделам по порядку меню");

    partial void OnGlobalHotkeysEnabledChanged(bool value)
    {
        ThemeManager.GlobalHotkeysEnabled = value;
    }

    private void RefreshStatusTexts()
    {
        ScuStatusText = L.T(IsSCUAvailable ? "да" : "нет");
    }

    // Стартовая инициализация: только «Главная» и «Бэнчмарк» (п. 13 аудита).
    // Остальные разделы инициализируются лениво при первом открытии
    // (EnsureSectionInitialized) — заставка исчезает значительно быстрее.
    public async Task InitializeAsync()
    {
        await RunSectionInitAsync("Dashboard", () => Dashboard.InitializeAsync()).ConfigureAwait(true);
        await RunSectionInitAsync("Benchmark", () => Benchmark.InitializeAsync()).ConfigureAwait(true);

        // Автозагрузка прогружается при старте: метрика «Элементов автозагрузки»
        // бэнчмарка должна быть читаемой сразу, а не «Не удалось прочитать».
        // Тот же ленивый init (InitializeAsync + refresh), помечается как загруженный.
        EnsureSectionInitialized(7);

        // Бэнчмарк запускается сразу после InitializeAsync (App.RunStartupAsync →
        // TriggerInitialBenchmark): дожидаемся инициализации автозагрузки, иначе
        // метрика «Элементов автозагрузки» считает состояние до загрузки списка.
        if (_startupInitTask is { } startupInit)
        {
            await startupInit.ConfigureAwait(true);
        }

        // Тихое автообновление базы сканера с GitHub Releases при старте.
        TaskRunner.RunAndForget(Scanner.AutoUpdateDatabaseAsync(), _logger, "scanner db auto-update");

        // «К применению» на «Главной»: статусы Dashboard уже прочитаны.
        Dashboard.RecomputePending();

        // Пока приложение открыто: тихая проверка GitHub Releases — уведомление
        // только если вышла новая версия (в «Настройках» есть и ручная проверка).
        // Fire-and-forget: заставка не должна ждать сетевой запрос и таймаут.
        TaskRunner.RunAndForget(CheckForUpdatesQuietAsync(), _logger, "update check");
    }

    private Dictionary<int, Func<Task>>? _sectionInits;
    private readonly HashSet<int> _initializedSections = [];

    // Задача ленивой инициализации автозагрузки (раздел 7) — InitializeAsync
    // дожидается её перед автозапуском бэнчмарка.
    private Task? _startupInitTask;

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
            // await вместо ContinueWith: ContinueWith запускает continuation даже после
            // сбоя InitializeAsync и сам завершается успешно — ошибка терялась молча.
            [4] = async () =>
            {
                await Bloat.InitializeAsync().ConfigureAwait(true);
                await ExecuteRefreshAsync(Bloat.RefreshCommand).ConfigureAwait(true);
            },
            [5] = () => ExecuteRefreshAsync(Privacy.RefreshCommand),
            [6] = async () =>
            {
                await Services.InitializeAsync().ConfigureAwait(true);
                await ExecuteRefreshAsync(Services.RefreshCommand).ConfigureAwait(true);
            },
            [7] = async () =>
            {
                await Startup.InitializeAsync().ConfigureAwait(true);
                await ExecuteRefreshAsync(Startup.RefreshCommand).ConfigureAwait(true);
            },
            [8] = () => ExecuteRefreshAsync(Power.RefreshCommand),
            [9] = () => ExecuteRefreshAsync(Network.RefreshCommand),
            [10] = () => ExecuteRefreshAsync(Ui.RefreshCommand),
            [11] = () => ExecuteRefreshAsync(Input.RefreshCommand),
            [12] = () => ExecuteRefreshAsync(Maintenance.RefreshCommand),
            [13] = () => ExecuteRefreshAsync(Security.RefreshCommand),
            [15] = async () =>
            {
                await Tasks.InitializeAsync().ConfigureAwait(true);
                await ExecuteRefreshAsync(Tasks.RefreshCommand).ConfigureAwait(true);
            },
            [16] = () => ExecuteRefreshAsync(Update.RefreshCommand),
            [25] = () => ExecuteRefreshAsync(Drivers.RefreshCommand),
            [19] = () => ExecuteRefreshAsync(Apps.RefreshCommand),
        };

        if (!_sectionInits.TryGetValue(number.Value, out var init))
        {
            return;
        }

        // Fire-and-forget: ошибки внутри RunSectionInitAsync логируются.
        var initTask = RunSectionInitAsync("lazy-" + number.Value, init);
        if (number.Value == 7)
        {
            _startupInitTask = initTask;
        }
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

    public DriversViewModel Drivers { get; }

    public ScannerViewModel Scanner { get; }

    public BrowserViewModel Browser { get; }

    public DeepSeekViewModel DeepSeek { get; }

    public TroubleshootingViewModel Troubleshooting { get; }

    [ObservableProperty]
    private SectionItem? currentSection;

    [ObservableProperty]
    private bool isAdmin;

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

    [ObservableProperty]
    private AppAccent iconAccentMode;

    partial void OnIconAccentModeChanged(AppAccent value)
    {
        ThemeManager.SetIconAccent(value);
        // SetIconAccent меняет статическое свойство — сохраняем общий settings.json.
        ThemeManager.SaveSettings(ThemeMode, AccentMode, Language, ShowLog, UacConfirmations);
        OnPropertyChanged(nameof(IconAccentBrush));
        RefreshAccentChoices();
    }

    // Квадрат текущего цвета иконки.
    public Brush IconAccentBrush => new SolidColorBrush(ThemeManager.CurrentIconAccentColor);

    partial void OnAccentModeChanged(AppAccent value)
    {
        ThemeManager.SetAccent(value);
        ThemeManager.ApplyTheme(ThemeMode);
        ThemeManager.SaveSettings(ThemeMode, value, Language, ShowLog, UacConfirmations);
        RefreshAccentChoices();
    }

    private void RefreshAccentChoices()
    {
        OnPropertyChanged(nameof(AccentChoices));
        OnPropertyChanged(nameof(IconAccentChoices));
        OnPropertyChanged(nameof(IconAccentBrush));
    }

    // Цвета для «квадратика» акцента: все пресеты, КРОМЕ текущего —
    // установленный цвет не дублируется в палитре выбора.
    public IReadOnlyList<AccentChoice> AccentChoices => BuildAccentChoices(AccentMode);

    // Цвета для «квадратика» иконки приложения — аналогично, без текущего.
    public IReadOnlyList<AccentChoice> IconAccentChoices => BuildAccentChoices(IconAccentMode);

    private static readonly AppAccent[] AccentChoiceOrder =
    [
        AppAccent.Blue, AppAccent.SkyBlue, AppAccent.Purple, AppAccent.Green, AppAccent.Orange,
    ];

    private IReadOnlyList<AccentChoice> BuildAccentChoices(AppAccent current)
    {
        var dark = ThemeManager.IsDarkTheme(ThemeMode);
        return AccentChoiceOrder
            .Where(mode => mode != current)
            .Select(mode => new AccentChoice(mode, new SolidColorBrush(ThemeManager.GetPresetColor(mode, dark))))
            .ToList();
    }

    public void ApplyAccent(AppAccent mode) => AccentMode = mode;

    public void ApplyIconAccent(AppAccent mode) => IconAccentMode = mode;

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

    // Индекс для сегментированного контроля «Настроек»: 0 = «Тусклый», 1 = «Насыщенный».
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

    [ObservableProperty]
    private bool globalHotkeysEnabled;

    // Глобальное затемнение при прогрузке/обновлении открытой вкладки: флаг
    // следит за IsBusy того раздела, который открыт сейчас (слушает MainWindow).
    [ObservableProperty]
    private bool isCurrentSectionBusy;

    // Разделы с ленивой полной прогрузкой карточек: источник флага IsBusy.
    private Dictionary<int, ObservableObject>? _busyTrackedSections;

    private Dictionary<int, ObservableObject> BusyTrackedSections => _busyTrackedSections ??= new()
    {
        [1] = Info, [2] = Components, [4] = Bloat, [5] = Privacy, [6] = Services,
        [7] = Startup, [8] = Power, [9] = Network, [10] = Ui, [11] = Input,
        [12] = Maintenance, [13] = Security, [15] = Tasks, [16] = Update, [19] = Apps,
        [23] = Troubleshooting, [25] = Drivers,
    };

    private static bool GetIsBusy(ObservableObject viewModel) =>
        viewModel.GetType().GetProperty("IsBusy")?.GetValue(viewModel) is true;

    private void UpdateCurrentSectionBusy()
    {
        IsCurrentSectionBusy = CurrentSection is { } section
            && BusyTrackedSections.TryGetValue(section.Number, out var viewModel)
            && GetIsBusy(viewModel);
    }

    private void OnTrackedSectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "IsBusy")
        {
            UpdateCurrentSectionBusy();
        }
    }

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

    public string AboutStatePath => SCU.Infrastructure.Storage.DashboardStatePaths.Directory;

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

    // ===================== IScuAiEnvironment (п. 30 ТЗ) =====================
    // Навигация AI использует тот же механизм, что клик по сайдбару:
    // SelectSectionByNumber + SectionHighlightRequested. Нового navigation
    // host нет (п. 38 тз — не дублировать).

    int? IScuAiEnvironment.CurrentSectionNumber => CurrentSection?.Number;

    string IScuAiEnvironment.CurrentSectionTitle => CurrentSection?.Title ?? string.Empty;

    // Последняя подсвеченная утилита: SectionHighlightRequested несёт заголовок
    // строки; id нет — он разрезается по заголовку из реестра Dashboard.
    string? IScuAiEnvironment.CurrentUtilityId => _currentUtilityTitle is null
        ? null
        : Dashboard.Utilities.FirstOrDefault(utility =>
            string.Equals(utility.ResolveTitle(), _currentUtilityTitle, StringComparison.Ordinal))?.Id;

    string? IScuAiEnvironment.CurrentUtilityTitle => _currentUtilityTitle;

    bool IScuAiEnvironment.IsAdmin => IsAdmin;

    IReadOnlyList<int> IScuAiEnvironment.AvailableSections =>
        Sections.Where(section => section.Number >= 0).Select(section => section.Number).ToList();

    bool IScuAiEnvironment.NavigateToSection(int sectionNumber)
    {
        SelectSectionByNumber(sectionNumber);
        return true;
    }

    void IScuAiEnvironment.HighlightUtility(int sectionNumber, string? utilityTitle)
    {
        _currentUtilityTitle = utilityTitle;
        SectionHighlightRequested?.Invoke(sectionNumber, utilityTitle);
    }

    private string? _currentUtilityTitle;

    // ===================== Сборка индекса справки AI (п. 4 ТЗ) =====================
    // Разделы — из того же меню, что видит пользователь; утилиты — из реестра
    // Dashboard; переключатели — из тех же строк разделов UI/Input; ⓘ-тексты —
    // из каталога I_*. Пересобирается при смене языка (ScuHelpService слушает
    // L.LanguageChanged), сам обход идёт только в момент сборки.
    private IReadOnlyList<ScuHelpEntry> BuildHelpIndex()
    {
        var builder = new ScuHelpIndexBuilder(key =>
            Application.Current?.TryFindResource(key) as string);

        foreach (var section in Sections)
        {
            builder.AddSection(section.Number);
        }

        foreach (var utility in Dashboard.Utilities)
        {
            builder.AddUtility(utility);
        }

        foreach (var row in Ui.ExplorerRows.Concat(Ui.VisualFxRows).Append(Ui.RecommendedRow))
        {
            builder.AddSwitch(row.Id, 10, row);
        }

        foreach (var row in Input.Rows)
        {
            builder.AddSwitch(row.Id, 11, row);
        }

        foreach (var (infoKey, sectionNumber, utilityId) in ScuHelpInfoCatalog.Entries)
        {
            builder.AddInfo(infoKey, sectionNumber, utilityId);
        }

        return builder.Build();
    }

    // ===================== Реестр AI tools (п. 6/8 ТЗ) =====================
    // Набор фиксирован: только эти typed tools и существуют. Любой другой
    // «инструмент» отвергается registry (allowlist).
    private static IReadOnlyList<IScuAiTool> CreateAiTools(
        ScuAiToolDeps deps,
        ScuAiPlanExecutor executor) =>
    [
        new ScuSearchHelpTool(deps),
        new ScuGetHelpTool(deps),
        new ScuGetContextTool(),
        new ScuOpenSectionTool(deps),
        new ScuFocusUtilityTool(deps),
        new ScuGetSystemSummaryTool(deps),
        new ScuGetNetworkStateTool(deps),
        new ScuGetPowerStateTool(deps),
        new ScuGetPrivacyStateTool(deps),
        new ScuGetUiStateTool(deps),
        new ScuGetInputStateTool(deps),
        new ScuGetServicesStateTool(deps),
        new ScuGetStartupStateTool(deps),
        new ScuGetUpdateStateTool(deps),
        new ScuGetMaintenanceStateTool(deps),
        new ScuGetTroubleshootingStateTool(deps),
        new ScuPrepareChangeTool(deps),
        new ScuApplyChangeTool(executor),
        new ScuRunDiagnosticsTool(deps),
        new ScuListAppFilesTool(),
        new ScuReadAppFileTool(),
    ];

    partial void OnCurrentSectionChanged(SectionItem? value)
    {
        CurrentSectionChanged?.Invoke(value?.Number);
        EnsureSectionInitialized(value?.Number);
        UpdateCurrentSectionBusy();

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

        // Открытие вкладки «Очистка»: пересчёт актуального объёма для очистки.
        if (value?.Number == 3)
        {
            TaskRunner.RunAndForget(Cleanup.ActivateAsync(), _logger, "cleanup activate");
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
        // Вместо системного MessageBox — карточка центра уведомлений в стиле приложения.
        SystemNotificationInterceptor.ReportWarning(L.T("Права администратора"), result.Message);
    }

    private const int MaxLogLines = 5000;

    private void OnLogLineWritten(string line)
    {
        // Браузер работает в отдельном окне: его рабочие события (INFO) не
        // дублируются в терминал приложения — остаются в файле журнала,
        // в терминал попадают только предупреждения и ошибки.
        if (line.Contains("BROWSER |", StringComparison.Ordinal)
            && line.Contains(" INFO |", StringComparison.Ordinal))
        {
            return;
        }

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
        Browser.Dispose();
        Troubleshooting.Dispose();
    }
}

// Элемент палитры «квадратика» в «Настройках»: режим акцента и его кисть
// для текущей темы (тёмной/светлой).
public sealed record AccentChoice(AppAccent Mode, Brush Brush);
