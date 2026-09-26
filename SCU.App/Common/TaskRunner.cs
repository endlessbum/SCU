namespace SCU.Common;

public static class TaskRunner
{
    // CancellationToken у Task.Run не прерывает уже начавшийся синхронный вызов.
    // Здесь токен проверяется до и после операции: отмена не создаёт ложного «моментального»
    // прерывания, но и не позволяет старому результату вернуться в VM после отмены.
    public static Task<T> RunBlocking<T>(Func<T> action, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var result = action();
            ct.ThrowIfCancellationRequested();
            return result;
        }, CancellationToken.None);

    // Перегрузка для операций без результата: та же проверка токена до и после.
    public static Task RunBlocking(Action action, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            action();
            ct.ThrowIfCancellationRequested();
        }, CancellationToken.None);

    // Запуск фоновой задачи без ожидания. Голый `_ = task` терял исключение до
    // финализатора GC (UnobservedTaskException всплывает в неопределённый момент);
    // здесь отмена глотается, а сбой сразу попадает в журнал.
    public static async void RunAndForget(Task task, Logger? logger = null, string context = "")
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Осознанная отмена — не ошибка.
        }
        catch (Exception exception)
        {
            logger?.Error("BACKGROUND | " + (context.Length > 0 ? context + " | " : string.Empty) + exception);
        }
    }
}
