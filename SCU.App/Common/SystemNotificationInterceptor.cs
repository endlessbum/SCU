namespace SCU.Common;

// Перехватчик системных событий, связанных с SCU: необработанные исключения
// фоновых потоков и незаблюдаемых задач, которые Windows иначе показала бы
// системным диалогом (или проглотила молча), выводятся карточками центра
// уведомлений (AppNotificationCenter) — в том же стиле, что и UAC-подтверждения
// приложения.
//
// Фатальные пути (вторая копия приложения, отказ старта, критическая ошибка
// UI-потока) остаются системными MessageBox: на момент события главное окно ещё
// не создано либо закрывается сразу — тост не проживёт свои шесть секунд.
public static class SystemNotificationInterceptor
{
    private const int MaxMessageLength = 300;

    private static Logger? _logger;
    private static bool _attached;

    // Подписка — один раз за запуск приложения (после создания журнала).
    public static void Attach(Logger logger)
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _logger = logger;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // Исключение фонового потока: .NET завершит процесс — карточка best-effort
    // (появится, только если диспетчер успеет отработать), журнал пишется всегда.
    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception exception)
        {
            TryLog("INTERCEPT | AppDomain | неизвестный объект исключения", level: false);
            return;
        }

        TryLog("INTERCEPT | AppDomain | " + exception, level: false);
        Push(L.T("Ошибка приложения"), Format(exception), AppNotificationKind.Danger);
    }

    // Незаблюдаемая задача: приложение продолжает работу, поэтому пользователь
    // обязан увидеть сбой — иначе фоновая операция падает «молча».
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryLog("INTERCEPT | Task | " + e.Exception, level: true);
        e.SetObserved();
        Push(L.T("Ошибка фоновой операции"), Format(e.Exception), AppNotificationKind.Warn);
    }

    // Немодальные события вне глобальных подписок (например, отказ повышения
    // прав) тоже выдаются в стиле приложения вместо системных MessageBox.
    public static void ReportWarning(string title, string message) =>
        Push(title, message, AppNotificationKind.Warn);

    // Информационные события (например, «SCU уже запущен» — сигнал второй копии)
    // — не ошибка, но пользователь должен видеть их в том же стиле приложения.
    public static void ReportInfo(string title, string message) =>
        Push(title, message, AppNotificationKind.Info);

    // Текст карточки: корневое исключение — тип и сообщение, без стеков; обрезка
    // до разумной длины (тост маленький). Полный стек остаётся в журнале.
    internal static string Format(Exception exception)
    {
        var root = exception;
        while (true)
        {
            var inner = root is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
                ? aggregate.InnerExceptions[0]
                : root.InnerException;
            if (inner is null)
            {
                break;
            }

            root = inner;
        }

        var typeName = root.GetType().Name;
        var text = string.IsNullOrEmpty(root.Message) ? typeName : typeName + ": " + root.Message;
        return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength] + "…";
    }

    // Push под защитой: перехватчик не должен сам стать источником падения
    // (центр уведомлений живёт на UI-потоке и молчит без Application.Current).
    private static void Push(string title, string message, AppNotificationKind kind)
    {
        try
        {
            AppNotificationCenter.Instance.Push(title, message, kind);
        }
        catch (Exception exception)
        {
            TryLog("INTERCEPT | push failed | " + exception.Message, level: true);
        }
    }

    // Журнал под защитой — по той же причине, что и в App: упавший лог внутри
    // обработчика необработанного исключения закрутил бы рекурсию.
    private static void TryLog(string message, bool level)
    {
        try
        {
            if (level)
            {
                _logger?.Warn(message);
            }
            else
            {
                _logger?.Error(message);
            }
        }
        catch
        {
            try
            {
                Console.Error.WriteLine(message);
            }
            catch
            {
            }
        }
    }
}
