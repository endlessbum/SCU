using System.Globalization;
using System.Text;

namespace SCU.Interop;

// Общий построчный декодер вывода консольных процессов. Консольные утилиты пишут
// в разных кодировках (netsh на Win11 — UTF-8, powercfg/attrib/PowerShell 5.1 — OEM),
// заранее не угадать. Каждая строка декодируется эвристикой: валидный UTF-8 с не-ASCII → UTF-8,
// иначе OEM/CP866/CP1251 — побеждает вариант с бо́льшим числом кириллических символов.
public static class ConsoleOutputDecoder
{
    // Максимум повторов чтения после разового IOException (см. ReadLinesAsync).
    private const int MaxIoRetries = 2;

    static ConsoleOutputDecoder()
    {
        // Регистрация провайдера кодировок один раз на класс.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // OEM-кодировка системы; Console.OutputEncoding тут не годится —
    // ModuleInitializer приложения выставляет её в UTF-8.
    private static readonly Encoding ProcEncoding = CreateProcEncoding();

    private static Encoding CreateProcEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    // Чтение сырых байт с построчным декодированием.
    // Глотаются только IOException/ObjectDisposedException от самого потока — пайп
    // закрывается вместе с процессом (kill/выход), это нормальное окончание чтения.
    // Исключения колбэка onLine пробрасываются наверх: раннер превращает их в
    // Result.Failure вместо тихой потери остатка вывода.
    public static async Task ReadLinesAsync(Stream baseStream, Action<string> onLine, CancellationToken ct)
    {
        var buffer = new List<byte>(4096);
        var readBuffer = new byte[4096];
        var ioRetries = 0;
        while (true)
        {
            int read = 0;
            try
            {
                read = await baseStream.ReadAsync(readBuffer.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                // Поток может закрываться одновременно с отменой операции при завершении приложения.
                break;
            }
            catch (IOException) when (ioRetries < MaxIoRetries && !ct.IsCancellationRequested)
            {
                // Разовое IO-исключение не обязано быть концом вывода: раньше часть
                // вывода терялась молча. Краткий retry — если пайп закрылся вместе с
                // процессом (нормальное окончание), все попытки истекают мгновенно.
                ioRetries++;
            }
            catch (IOException)
            {
                // Закрытие анонимного pipe после завершения процесса считается концом вывода.
                break;
            }

            if (read == 0)
            {
                break;
            }

            ioRetries = 0;

            for (var i = 0; i < read; i++)
            {
                var b = readBuffer[i];
                if (b == '\n')
                {
                    EmitLine(buffer, onLine);
                }
                else if (b != '\r')
                {
                    buffer.Add(b);
                }
            }
        }

        if (buffer.Count > 0)
        {
            EmitLine(buffer, onLine);
        }
    }

    private static void EmitLine(List<byte> buffer, Action<string> onLine)
    {
        var bytes = buffer.ToArray();
        buffer.Clear();
        onLine(DecodeLine(bytes));
    }

    public static string DecodeLine(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes.All(b => b < 0x80))
        {
            return Encoding.ASCII.GetString(bytes);
        }

        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            if (utf8.Any(c => c > 127))
            {
                return utf8;
            }
        }
        catch
        {
            // Не UTF-8 — пробуем однобайтовые кодировки.
        }

        string? best = null;
        var bestCyrillic = -1;
        var bestPlausibility = -1;
        foreach (var encoding in CandidateEncodings())
        {
            try
            {
                var candidate = encoding.GetString(bytes);
                var cyrillic = candidate.Count(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё');
                if (cyrillic > bestCyrillic)
                {
                    bestCyrillic = cyrillic;
                    best = candidate;
                    bestPlausibility = RussianPlausibility(candidate);
                }
                else if (cyrillic == bestCyrillic)
                {
                    // Равное число кириллических символов: верхний регистр и «а-п»
                    // декодируются в кириллицу в обеих кодировках. Побеждает текст
                    // с более правдоподобными русскими биграммами (CP1251-вывод
                    // ANSI-утилит раньше всегда проигрывал CP866).
                    var plausibility = RussianPlausibility(candidate);
                    if (plausibility > bestPlausibility)
                    {
                        bestCyrillic = cyrillic;
                        best = candidate;
                        bestPlausibility = plausibility;
                    }
                }
            }
            catch
            {
                // Кодировка недоступна — пропускаем.
            }
        }

        return best ?? Encoding.ASCII.GetString(bytes);
    }

    // Частотные русские биграммы; правдоподобие = число пар соседних кириллических
    // букв из набора. Не языковая модель — только тай-брейк между кодировками.
    private static readonly HashSet<string> CommonBigrams = new(new[]
    {
        "ст", "но", "ен", "то", "на", "ов", "ни", "ра", "во", "ко",
        "ес", "ос", "ти", "не", "го", "ла", "ан", "по", "ем", "ор",
        "ет", "ка", "та", "ли", "за", "ом", "ма", "ре", "ыт", "ме",
        "от", "ам", "ал", "ле", "оп", "ис", "до", "ск", "ый", "ич"
    });

    private static int RussianPlausibility(string line)
    {
        var score = 0;
        char previous = default;
        for (var i = 0; i < line.Length; i++)
        {
            var current = char.ToLowerInvariant(line[i]);
            var isCyrillic = current is >= 'а' and <= 'я' or 'ё';
            if (isCyrillic && previous != default && CommonBigrams.Contains(
                    string.Concat(previous, current)))
            {
                score++;
            }

            previous = isCyrillic ? current : default;
        }

        return score;
    }

    private static IEnumerable<Encoding> CandidateEncodings()
    {
        foreach (var codePage in new[] { 866, 1251 })
        {
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(codePage);
            }
            catch
            {
                continue;
            }

            yield return encoding;
        }

        yield return ProcEncoding;
    }
}
