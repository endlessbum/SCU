using System.Windows;

namespace SCU.Common;

// Общая обёртка эксклюзивной операции раздела (аудит: 18 идентичных копий
// RunExclusiveAsync расходились guard'ами и лог-тегами; теперь тело одно).
// Вызывающая VM делегирует состояние: IsBusy, CTS, StatusText.
public static class SectionOperations
{
    public static async Task RunExclusiveAsync(
        string title,
        string logTag,
        Func<bool> isBusy,
        Action<bool> setBusy,
        Func<CancellationTokenSource?> getCts,
        Action<CancellationTokenSource?> setCts,
        Action<string> setStatus,
        Logger logger,
        Func<CancellationToken, Task> action)
    {
        if (isBusy())
        {
            return;
        }

        getCts()?.Dispose();
        setCts(new CancellationTokenSource());
        setBusy(true);
        try
        {
            await action(getCts()!.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            setStatus(L.T("Операция отменена."));
            logger.Warn("CANCEL | " + title);
        }
        catch (Exception exception)
        {
            setStatus(L.T("Ошибка: {0}", exception.Message));
            logger.Error(logTag + " | " + title + " | " + exception);
        }
        finally
        {
            setBusy(false);
            // Токен завершённой операции больше не раздаётся: CTS закрывается
            // сразу, а не при старте следующей операции — поздний Register на
            // dispose-нутый CTS (фоновое продолжение) давал бы ObjectDisposedException.
            getCts()?.Dispose();
            setCts(null);
        }
    }
}
