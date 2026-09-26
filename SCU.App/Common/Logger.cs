using System.Diagnostics;
using System.Text;

namespace SCU.Common;

public sealed class Logger
{
    private const long MaxLogFileBytes = 5 * 1024 * 1024;
    private const int MaxArchivedLogFiles = 3;
    // Ограничение журнала в памяти (аналог MainViewModel.MaxLogLines): длинные операции
    // (DISM/SFC) дают тысячи строк.
    private const int MaxMemoryLines = 5000;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly object _gate = new();
    private readonly List<string> _memory = new();
    private StreamWriter? _writer;

    private Logger(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public event Action<string>? LineWritten;

    public static Logger CreateForCurrentRun()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SCU", "logs")
        };

        Exception? lastError = null;
        foreach (var logsDirectory in candidates)
        {
            try
            {
                Directory.CreateDirectory(logsDirectory);

                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var path = Path.Combine(logsDirectory, $"SCU_{stamp}.log");
                var suffix = 1;
                while (File.Exists(path))
                {
                    path = Path.Combine(logsDirectory, $"SCU_{stamp}_{suffix}.log");
                    suffix++;
                }

                File.WriteAllText(path, string.Empty, Utf8NoBom);
                return new Logger(path);
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
        }

        throw new InvalidOperationException("Не удалось создать файл журнала.", lastError);
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _memory.ToArray();
        }
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message) => Write("ERROR", message);

    public void Warn(string message) => Write("WARN", message);

    public void Raw(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // Строки скрипта пишутся в файл как есть (без метки времени и префикса уровня):
        // AppendLine метку времени не добавляет — добавлять её только для Raw не нужно.
        lock (_gate)
        {
            AppendLine(message);
            AddToMemory(message);
            // Событие внутри lock: подписчики получают строки в порядке файла,
            // а не в порядке выхода параллельных писателей.
            RaiseLineWritten(message);
        }

        Debug.WriteLine(message);
    }

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] APP | {level} | {message}";

        lock (_gate)
        {
            AppendLine(line);
            AddToMemory(line);
            // Событие внутри lock — см. Raw: порядок строк UI == порядок файла.
            RaiseLineWritten(line);
        }

        Debug.WriteLine(line);
    }

    // Журнал в памяти ограничен: старейшие строки вытесняются.
    private void AddToMemory(string line)
    {
        _memory.Add(line);
        if (_memory.Count > MaxMemoryLines)
        {
            _memory.RemoveAt(0);
        }
    }

    private void AppendLine(string line)
    {
        try
        {
            lock (_gate)
            {
                // Пишем в постоянно открытый writer (flush на каждой строке — хвост
                // лога виден внешним просмотрщикам, как и раньше). Открытие/закрытие
                // файла на каждую строку при тысячах строк DISM/SFC стоило заметно
                // дороже самой записи.
                if (_writer is null)
                {
                    _writer = OpenWriter();
                }

                if (RotateIfNeeded())
                {
                    // Ротация переименовала текущий файл: handle смотрит уже в архивную
                    // копию — переоткрываемся на новом пустом файле.
                    _writer.Dispose();
                    _writer = OpenWriter();
                }

                _writer.WriteLine(line);
                _writer.Flush();
            }
        }
        catch (Exception exception)
        {
            // Writer мог сломаться (внешняя блокировка, удаление файла): сбрасываем —
            // следующая строка переоткроет. Потеря одной строки лучше падения операции.
            try
            {
                _writer?.Dispose();
            }
            catch
            {
            }

            _writer = null;
            Debug.WriteLine($"[LOGGER-ERROR] {exception}");
        }
    }

    private StreamWriter OpenWriter() => new(
        new FileStream(
            FilePath,
            FileMode.Append,
            FileAccess.Write,
            // Delete — чтобы ротация могла переименовать открытый файл; ReadWrite —
            // чтобы SCU.ps1 продолжал дописывать в тот же лог.
            FileShare.ReadWrite | FileShare.Delete),
        Utf8NoBom);

    // true — файл был ротирован (архивные копии сдвинуты, текущий усечён).
    private bool RotateIfNeeded()
    {
        if (!File.Exists(FilePath))
        {
            return false;
        }

        if (new FileInfo(FilePath).Length < MaxLogFileBytes)
        {
            return false;
        }

        // Файл может быть занят внешним процессом (SCU.ps1 пишет в тот же лог):
        // сбой ротации не должен терять текущую строку — она уйдёт в существующий
        // файл, а не будет молча проглочена исключением выше по стеку.
        try
        {
            for (var index = MaxArchivedLogFiles; index >= 1; index--)
            {
                var source = index == 1 ? FilePath : FilePath + "." + (index - 1);
                var destination = FilePath + "." + index;
                if (File.Exists(source))
                {
                    File.Move(source, destination, overwrite: true);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LOGGER] rotation shift skipped: {exception.Message}");
            return false;
        }

        try
        {
            File.WriteAllText(FilePath, string.Empty, Utf8NoBom);
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LOGGER] rotation truncate skipped: {exception.Message}");
            return false;
        }
    }

    private void RaiseLineWritten(string line)
    {
        try
        {
            LineWritten?.Invoke(line);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LOGGER-ERROR] {exception}");
        }
    }

}
