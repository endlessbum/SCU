namespace SCU.Common;

// Защита запуска скачанного исполняемого файла от TOCTOU (SCU-02): блокирующий
// дескриптор открывается ДО проверки подписи, поэтому подменить или удалить
// файл между проверкой и запуском невозможно — пока дескриптор жив, Windows не
// даст другому процессу открыть файл на запись/удаление/переименование.
// Возврат null — запуск запрещён, причина в журнале.
// Вызывающая сторона держит FileStream в using до старта процесса.
public static class VerifiedLaunch
{
    public static FileStream? OpenLocked(string path, Logger logger)
    {
        FileStream? lockStream;
        try
        {
            // Share.Read: параллельное чтение разрешено, запись/удаление/переименование
            // другими процессами — нет. Дескриптор «прикалывает» объект к пути на всё
            // время проверки: предыдущий порядок (проверка по пути → открытие) оставлял
            // гонку между проверкой и открытием, замена файла в этом окне запускалась
            // бы непроверенной (SCU-02).
            lockStream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
        }
        catch (Exception exception)
        {
            logger.Error("LAUNCH | refused | read lock failed | " + exception.Message);
            return null;
        }

        try
        {
            // Объект уже закреплён дескриптором — проверка reparse/подписи относится
            // ровно к тому файлу, который будет запущен.
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                logger.Error("LAUNCH | refused | reparse point | " + path);
                lockStream.Dispose();
                return null;
            }

            // Повторная проверка: первая была при скачивании — окно между ней и
            // Process.Start закрывается проверкой непосредственно перед запуском.
            var verify = SignatureVerifier.VerifyMicrosoftSigned(path);
            if (!verify.IsSuccess)
            {
                logger.Error("LAUNCH | refused | pre-start signature | " + verify.Message);
                lockStream.Dispose();
                return null;
            }

            return lockStream;
        }
        catch (Exception exception)
        {
            logger.Error("LAUNCH | refused | post-lock check failed | " + exception.Message);
            lockStream.Dispose();
            return null;
        }
    }
}
