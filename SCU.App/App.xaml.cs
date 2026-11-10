using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SCU.Common;
using SCU.Infrastructure.Windows;
using SCU.Interop;

namespace SCU;

public partial class App : Application
{
    public static bool RunInBackgroundOnClose { get; set; }

    // Идёт завершение сеанса Windows (выход/выключение): закрытие окна не
    // отменяем, иначе ОС показывает «приложение препятствует завершению работы».
    // Ставится в OnStartup подпиской на SessionEnding.
    public static bool IsSessionEnding { get; private set; }

    // Автозапуск (Run\SCU передаёт --tray): приложение стартует свёрнутым в трей.
    // Читается один раз из аргументов командной строки при старте.
    public static bool StartInTray { get; private set; }
    private Logger? _logger;

    // Один экземпляр приложения: два окна SCU одновременно правили бы реестр
    // и службы, бэкапы перезаписывали бы друг друга.
    private static Mutex? _singleInstanceMutex;

    // Сигнальное событие «вторая копия запущена» — см. TryAcquireSingleInstanceMutex.
    private static EventWaitHandle? _alreadyRunningNotify;

    // Защита от каскада MessageBox: вложенный pump диспетчера внутри MessageBox.Show
    // может обработать следующее исключение и наслоить новый диалог до stack overflow.
    private bool _uiCrashDialogOpen;

    [ModuleInitializer]
    internal static void ConfigureBootstrap()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
        }

        AppDomain.CurrentDomain.UnhandledException += OnBootstrapUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // --tray: автозапуск после загрузки системы — окно не показываем,
        // приложение садится в область уведомлений (см. RunStartupAsync).
        StartInTray = e.Args.Any(a => a.Equals(ScuAutorun.TrayArg, StringComparison.OrdinalIgnoreCase));

        if (!TryAcquireSingleInstanceMutex())
        {
            NotifyRunningInstance();
            Shutdown(0);
            return;
        }

        StartAlreadyRunningListener();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += (_, _) => IsSessionEnding = true;

        // Цвет заставки читается ДО её создания (п. 3 запроса): логотип красится
        // в тот же цвет, что задан настройкой «Цвет иконки приложения».
        ThemeManager.SetIconAccent(ThemeManager.LoadIconAccent());

        // Тумблер «Анимации интерфейса»: XAML-триггеры читают ресурс со
        // стартовым True — реальное значение из settings.json нужно выставить
        // до создания окон (аудит: иначе после перезапуска XAML-анимации
        // работали вопреки выключенному тумблеру).
        Application.Current.Resources["UiAnimationsOn"] = ThemeManager.AnimationsEnabled;

        _ = RunStartupAsync(e, null);
    }

    // Заставка → фоновая загрузка данных всех вкладок → показ окна → гашение заставки.
    private async Task RunStartupAsync(StartupEventArgs e, SplashWindow? splash)
    {
        try
        {
            base.OnStartup(e);

            // Автозапуск (--tray): заставка не создаётся — при старте системы
            // приложение не показывает ни одного окна и молча садится в трей.
            if (splash is null && !StartInTray)
            {
                splash = new SplashWindow();
                splash.Show();
            }

            // П. №9 аудита: общий логгер на запуск — все потребители (инструменты
            // AI, QuarantineService, ServiceManager) пишут в один файл вместо
            // сирот SCU_<timestamp>.log на каждый конструктор.
            _logger = Logger.CurrentRun;
            Environment.SetEnvironmentVariable("SCU_LOGFILE", _logger.FilePath);
            ThemeManager.LogWarningSink = message => _logger.Warn(message);

            // Системные события, связанные с SCU (исключения фоновых потоков и
            // незаблюдаемых задач), перехватчик выводит карточками центра уведомлений
            // в стиле приложения. Критическая ошибка UI-потока — в OnStartup: она
            // фатальна, тост не успел бы прожить свою жизнь, остаётся модальный MessageBox.
            SystemNotificationInterceptor.Attach(_logger);

            // П.2: контрольный обход backup-каталогов — хранятся только последние 10 файлов.
            BackupRetention.EnforceAll(logger: _logger);

            // Язык интерфейса (settings.json → язык установщика → русский) применяется до создания окна.
            L.SetLanguage(L.LoadLanguage(), notify: false);

            // Единая политика presentation-форматирования (аудит): даты и числа во
            // всём интерфейсе — одна культура выбранного языка, а не культура ОС
            // (раньше смешивались InvariantCulture и CurrentCulture, разделители
            // дробей расходились между разделами). L.Current — это enum, поэтому
            // культуру подбираем сами: ru→ru-RU, en→en-US.
            try
            {
                var culture = new CultureInfo(L.Current == AppLanguage.En ? "en-US" : "ru-RU");
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch
            {
                // Кривая культура — не повод срывать старт: остаётся системная.
            }

            var isAdmin = Elevation.IsAdmin();
            // Тот же резолвер, что у SCURunner — INIT-лог показывает фактический путь.
            var scriptPath = SCURunner.FindScript();

            _logger.Info(
                $"INIT | admin={isAdmin} | scu={File.Exists(scriptPath)} | log={_logger.FilePath}");

            if (!isAdmin)
            {
                _logger.Warn("INIT | запущено без прав администратора; изменяющие операции недоступны");
            }

            WriteStartupHintToConsole(isAdmin, _logger.FilePath);

            var runner = new SCURunner(_logger);
            var window = new MainWindow(_logger, runner);
            MainWindow = window;

            // Смена темы/акцента Windows в рантайме применяется сразу (Auto/системный акцент).
            ThemeManager.StartSystemThemeWatcher();

            // Данные всех вкладок собираются, пока главное окно ещё не показано;
            // на экране заставка с индикатором загрузки (0% — левый край логотипа,
            // 100% — правый). Прогресс — доля завершённых разделов.
            var progress = new Progress<double>(p => splash?.SetProgress(p));
            await window.InitializeDataAsync(progress).ConfigureAwait(true);

            // Все данные собраны: индикатор пропадает, окно запускается сразу,
            // логотип гаснет через 2 секунды после исчезновения индикатора.
            // Автозапуск (--tray): окно создаётся, но сразу прячется — hwnd нужен,
            // чтобы ожили иконка трея и глобальная горячая клавиша
            // (SourceInitialized срабатывает на Show). Show и Hide выполняются
            // в одном такте диспетчера без отрисовки кадра — мигания нет,
            // заставка в этот момент всё ещё на экране.
            splash?.HideProgressIndicator();
            if (StartInTray)
            {
                // WPF запрещает Show при ShowActivated=false и WindowState=Maximized
                // (в XAML окно развёрнутое) — на время скрытого показа состояние
                // сбрасывается в Normal. После Hide возвращается Maximized: WPF
                // кэширует состояние скрытого окна и применит его при следующем
                // Show — подъём из трея снова развёрнутый.
                window.ShowActivated = false;
                window.WindowState = WindowState.Normal;
                window.Show();
                window.Hide();
                window.WindowState = WindowState.Maximized;
                window.ShowActivated = true;
                splash?.Close();
            }
            else
            {
                window.Show();
                splash.CloseAfterFade(TimeSpan.FromSeconds(2));
            }

            // Бэнчмарк запускается автоматически при старте, в фоне — стартовые
            // данные разделов уже собраны, окно отзывчиво во время сканирования.
            window.TriggerInitialBenchmark();
        }
        catch (Exception exception)
        {
            splash?.Close();
            TryLog("STARTUP | " + exception);
            WriteErrorToConsole("STARTUP | " + exception);
            MessageBox.Show(
                L.T("Не удалось запустить приложение") + ":\n" + exception.Message,
                "SCU",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool TryAcquireSingleInstanceMutex()
    {
        // Имя может быть занято объектом другого типа (namespace не различает
        // Mutex/Event/Semaphore) или недоступно: деградация — работаем без
        // single-instance guard, та же политика, что у notify-события ниже.
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, @"Local\SCU.SingleInstance");
        }
        catch (Exception exception)
        {
            _singleInstanceMutex = null;
            Console.Error.WriteLine("SINGLE INSTANCE | mutex unavailable | " + exception.Message);
            return true;
        }

        bool owned;
        try
        {
            owned = _singleInstanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // Предыдущая копия умерла, не отпустив mutex: владение уже перешло
            // к нам (исключение бросается ПОСЛЕ успешного захвата) — считаем
            // это штатным запуском вместо падения до перезагрузки.
            owned = true;
        }
        catch (Exception exception)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Console.Error.WriteLine("SINGLE INSTANCE | wait failed | " + exception.Message);
            return true;
        }

        if (!owned)
        {
            return false;
        }

        // Сигнальное событие «вторая копия запущена»: работающая копия слушает его
        // и показывает уведомление в стиле приложения. Без события (например,
        // имя занято объектом другого типа) — деградация: вторая копия покажет
        // системный MessageBox через fallback в NotifyRunningInstance.
        try
        {
            _alreadyRunningNotify = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, @"Local\SCU.SingleInstance.Notify");
        }
        catch (Exception exception)
        {
            _alreadyRunningNotify = null;
            Console.Error.WriteLine("SINGLE INSTANCE | notify event failed | " + exception.Message);
        }

        return true;
    }

    // Вторая копия не имеет ни окна, ни центра уведомлений: показать карточку в
    // стиле приложения может только работающая копия. Сигнализируем событие —
    // она сама выведет уведомление «уже запущено» и поднимет своё окно.
    private static void NotifyRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\SCU.SingleInstance.Notify", out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }

                return;
            }
        }
        catch
        {
            // Сигнализация не удалась — fallback ниже.
        }

        MessageBox.Show(
            L.T("SCU уже запущен") + " " + L.T("Одновременно может работать только одна копия приложения."),
            "SCU",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // Фоновый слушатель сигнала второй копии: сработал — карточка в стиле
    // приложения (перехватчик) и поднятие работающего окна. Поток фоновый,
    // завершается вместе с процессом; Dispose события при выходе гасит WaitOne
    // через ObjectDisposedException — это штатное окончание ожидания.
    private void StartAlreadyRunningListener()
    {
        var handle = _alreadyRunningNotify;
        if (handle is null)
        {
            return;
        }

        var listener = new Thread(() =>
        {
            try
            {
                while (handle.WaitOne())
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        SystemNotificationInterceptor.ReportInfo(
                            L.T("SCU уже запущен"),
                            L.T("Одновременно может работать только одна копия приложения."));
                        BringMainWindowToFront();
                    });
                }
            }
            catch (ObjectDisposedException)
            {
                // Событие удалено при выходе приложения — ожидание закончено.
            }
        })
        {
            IsBackground = true,
            Name = "SCU.AlreadyRunningListener",
        };
        listener.Start();
    }

    private void BringMainWindowToFront()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        // Окно может быть скрыто (хоткей, закрытие в трей): Activate на
        // невидимом окне ничего не покажет пользователю.
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // П. 12 аудита: дозаписываем события истории из фоновой очереди
        // перед выходом — ничего не теряется молча.
        // Поллинг внутри FlushPendingAsync (Task.Delay) уводим в пул потоков:
        // UI-поток на это время не блокируется, OnExit всё равно дождётся.
        try
        {
            Task.Run(() => SCU.Infrastructure.Storage.HistoryStore.FlushPendingAsync(TimeSpan.FromSeconds(2)))
                .GetAwaiter().GetResult();
        }
        catch
        {
        }

        _singleInstanceMutex?.Dispose();
        _alreadyRunningNotify?.Dispose();
        _logger?.Info($"EXIT | code={e.ApplicationExitCode}");
        // Writer лога не закрываем явно: фоновые readers внешних процессов могут
        // ещё доставить последние строки; при завершении процесса хэндл закроет ОС.
        base.OnExit(e);
    }

    private static void WriteStartupHintToConsole(bool isAdmin, string logPath)
    {
        try
        {
            Console.WriteLine("SCU запущен. Закройте окно приложения, чтобы вернуть управление в консоль.");
            Console.WriteLine("Администратор: " + (isAdmin ? "да" : "нет"));
            Console.WriteLine("Лог: " + logPath);
        }
        catch
        {
        }
    }

    private static void WriteErrorToConsole(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
        }
    }

    private static void OnBootstrapUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        WriteErrorToConsole("UNHANDLED | " + e.ExceptionObject);
    }

    // Лог в обработчиках критических исключений — под защитой: если добавление
    // строки в журнал (LogPane/TextBox) само бросит исключение, обработчик
    // влетел бы в рекурсию и закрутил UI-поток.
    private void TryLog(string message)
    {
        try
        {
            _logger?.Error(message);
        }
        catch
        {
            WriteErrorToConsole(message);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog("UNHANDLED | UI | " + e.Exception);

        if (_uiCrashDialogOpen)
        {
            // Диалог уже открыт: исключение пришло из вложенного pump диспетчера MessageBox.
            // Глотаем, чтобы не наслаивать ещё один MessageBox (риск stack overflow).
            e.Handled = true;
            return;
        }

        _uiCrashDialogOpen = true;
        try
        {
            MessageBox.Show(
                e.Exception.Message + "\n\n" + L.T("Подробности в журнале (файл лога SCU)."),
                L.T("Критическая ошибка"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _uiCrashDialogOpen = false;
        }

        // После необработанного UI-исключения состояние окна/VM может быть неконсистентным.
        // Разрешаем WPF выполнить стандартное аварийное завершение вместо продолжения работы.
        e.Handled = false;
    }
}