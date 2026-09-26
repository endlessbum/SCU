using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SCU.Common;
using SCU.Interop;

namespace SCU;

public partial class App : Application
{
    private Logger? _logger;

    // Один экземпляр приложения: два окна SCU одновременно правили бы реестр
    // и службы, резервы перезаписывали бы друг друга.
    private static Mutex? _singleInstanceMutex;

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
        if (!TryAcquireSingleInstanceMutex())
        {
            MessageBox.Show(
                "SCU уже запущен. Одновременно может работать только одна копия приложения.",
                "SCU",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Заставка показывается до любой тяжёлой работы: резко, без анимации появления.
        var splash = new SplashWindow();
        splash.Show();

        // Прокачка очереди диспетчера до приоритета Loaded: заставка успевает
        // отрисоваться до того, как UI-поток займёт инициализация окна и данных.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // П. 12 аудита: логика старта — Task-based (RunStartupAsync, ошибки
        // обрабатываются внутри); async void остаётся только тонкой обвязкой
        // на границе жизненного цикла приложения.
        _ = RunStartupAsync(e, splash);
    }

    // Заставка → фоновая загрузка данных всех вкладок → показ окна → гашение заставки.
    private async Task RunStartupAsync(StartupEventArgs e, SplashWindow splash)
    {
        try
        {
            base.OnStartup(e);

            _logger = Logger.CreateForCurrentRun();
            Environment.SetEnvironmentVariable("SCU_LOGFILE", _logger.FilePath);
            ThemeManager.LogWarningSink = message => _logger.Warn(message);

            // П.2: контрольный обход backup-каталогов — хранятся только последние 10 файлов.
            BackupRetention.EnforceAll(logger: _logger);

            // Язык интерфейса (settings.json → язык установщика → русский) применяется до создания окна.
            L.SetLanguage(L.LoadLanguage(), notify: false);

            var isAdmin = Elevation.IsAdmin();
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SCU.ps1");

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
            // на экране остаётся заставка.
            await window.InitializeDataAsync().ConfigureAwait(true);

            window.Show();
            // Окно уже на экране — только теперь заставка начинает гаснуть.
            splash.CloseAfterFade();

            // Бэнчмарк запускается автоматически при старте, в фоне — стартовые
            // данные разделов уже собраны, окно отзывчиво во время сканирования.
            window.TriggerInitialBenchmark();
        }
        catch (Exception exception)
        {
            splash.Close();
            TryLog("STARTUP | " + exception);
            WriteErrorToConsole("STARTUP | " + exception);
            MessageBox.Show(
                "Не удалось запустить приложение:\n" + exception.Message,
                "SCU",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool TryAcquireSingleInstanceMutex()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\SCU.SingleInstance", out var createdNew);
        return createdNew;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // П. 12 аудита: дозаписываем события истории из фоновой очереди
        // перед выходом — ничего не теряется молча.
        try
        {
            Services.Dashboard.HistoryStore.FlushPendingAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter().GetResult();
        }
        catch
        {
        }

        _singleInstanceMutex?.Dispose();
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
                e.Exception.Message,
                "Критическая ошибка",
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

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            TryLog("UNHANDLED | AppDomain | " + exception);
        }
        else
        {
            TryLog("UNHANDLED | AppDomain | неизвестный объект исключения");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryLog("UNHANDLED | Task | " + e.Exception);
        e.SetObserved();
    }
}
