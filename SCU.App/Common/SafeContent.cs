using System.Text;

namespace SCU.Common;

// Форматтеры безопасного просмотра (документ п. 22: сканируемый файл — только
// данные). Всё работает с уже прочитанными байтами: окно просмотра никогда
// не исполняет содержимое и не передаёт путь внешним программам.
public static class SafeContent
{
    // Лимиты просмотра: читаем не больше, чем указано, остальное — усечение.
    public const int MaxReadBytes = 1024 * 1024;
    public const int HexPreviewBytes = 64 * 1024;
    public const int MaxStrings = 10000;
    public const int MinStringLength = 5;

    // Эвристика «это текст»: NUL-байты — почти всегда бинарник; управляющие
    // символы (кроме \t\r\n) тоже. Высокие байты (>0x7E) текстом считаются —
    // это кириллица UTF-8/CP1251.
    public static bool IsLikelyText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return true;
        }

        int control = 0;
        const int maxControlPercent = 10;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == 0)
            {
                return false;
            }

            if (b < 0x20 && b is not (byte)'\t' and not (byte)'\r' and not (byte)'\n')
            {
                control++;
            }
        }

        return control * 100 < bytes.Length * maxControlPercent;
    }

    // Классический hex-дамп: смещение, 16 hex-байт, ASCII-колонка.
    public static string ToHexDump(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder((bytes.Length / 16 + 1) * 80);
        const string digits = "0123456789ABCDEF";
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            builder.Append(offset.ToString("X8"));
            builder.Append("  ");

            var end = Math.Min(offset + 16, bytes.Length);
            for (var i = 0; i < 16; i++)
            {
                if (offset + i < end)
                {
                    var b = bytes[offset + i];
                    builder.Append(digits[b >> 4]);
                    builder.Append(digits[b & 0x0F]);
                }
                else
                {
                    builder.Append("  ");
                }

                builder.Append(i == 7 ? "  " : ' ');
            }

            builder.Append(' ');
            for (var i = offset; i < end; i++)
            {
                var b = bytes[i];
                builder.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    // ASCII-строки (последовательности печатных символов) — самое полезное
    // для быстрого осмотра бинарника: URL, пути, сообщения.
    public static IReadOnlyList<string> ExtractStrings(ReadOnlySpan<byte> bytes, int minLength = MinStringLength)
    {
        var result = new List<string>();
        var start = -1;
        for (var i = 0; i <= bytes.Length; i++)
        {
            var printable = i < bytes.Length && bytes[i] is >= 0x20 and <= 0x7E;
            if (printable && start < 0)
            {
                start = i;
            }
            else if (!printable && start >= 0)
            {
                if (i - start >= minLength)
                {
                    result.Add(Encoding.ASCII.GetString(bytes.Slice(start, i - start)));
                    if (result.Count >= MaxStrings)
                    {
                        return result;
                    }
                }

                start = -1;
            }
        }

        return result;
    }

    // Декодирование текста: строгий UTF-8, при неудаче — CP1251
    // (CodePagesEncodingProvider регистрируется в ConsoleOutputDecoder/Startup).
    public static string DecodeText(byte[] bytes)
    {
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }
}
