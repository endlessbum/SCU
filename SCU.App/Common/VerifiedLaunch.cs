namespace SCU.Common;

// Защита запуска скачанного исполняемого файла от TOCTOU: подпись проверяется
// повторно непосредственно перед запуском, reparse point запрещается, read-handle
// удерживается до Process.Start — подменить или удалить файл в окне «после
// проверки» нельзя. Возврат null — запуск запрещён, причина в журнале.
// Вызывающая сторона держит FileStream в using до старта процесса.
public static class VerifiedLaunch
{
    public static FileStream? OpenLocked(string path, Logger logger)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                logger.Error("LAUNCH | refused | reparse point | " + path);
                return null;
            }
        }
        catch (Exception exception)
        {
            logger.Error("LAUNCH | refused | attributes unreadable | " + exception.Message);
            return null;
        }

        // Повторная проверка: первая была при скачивании — окно между ней и
        // Process.Start закрывается проверкой непосредственно перед запуском.
        var verify = SignatureVerifier.VerifyMicrosoftSigned(path);
        if (!verify.IsSuccess)
        {
            logger.Error("LAUNCH | refused | pre-start signature | " + verify.Message);
            return null;
        }

        try
        {
            // Share.Read: параллельное чтение разрешено, запись/удаление — нет.
            return new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
        }
        catch (Exception exception)
        {
            logger.Error("LAUNCH | refused | read lock failed | " + exception.Message);
            return null;
        }
    }
}
