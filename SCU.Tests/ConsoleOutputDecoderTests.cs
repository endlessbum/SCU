using System.Text;
using SCU.Interop;
using Xunit;

namespace SCU.Tests;

public class ConsoleOutputDecoderTests
{
    static ConsoleOutputDecoderTests()
    {
        // В тестовом процессе провайдер однобайтовых кодировок (866, 1251) может быть
        // ещё не зарегистрирован — регистрируем до создания тестовых байтов.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void DecodeLine_EmptyInput_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, ConsoleOutputDecoder.DecodeLine([]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ACCESS DENIED")]
    [InlineData("Done in 0.42s")]
    public void DecodeLine_PureAscii_PassesThroughUnchanged(string line)
    {
        Assert.Equal(line, ConsoleOutputDecoder.DecodeLine(Encoding.ASCII.GetBytes(line)));
    }

    [Fact]
    public void DecodeLine_Utf8Cyrillic_DecodedAsUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes("Не удалось выполнить операцию");

        Assert.Equal("Не удалось выполнить операцию", ConsoleOutputDecoder.DecodeLine(bytes));
    }

    [Fact]
    public void DecodeLine_Cp866Cyrillic_DecodedBackToSameText()
    {
        var original = "Не удалось выполнить операцию";

        var decoded = ConsoleOutputDecoder.DecodeLine(Encoding.GetEncoding(866).GetBytes(original));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeLine_Cp1251Cyrillic_DecodedBackToSameText()
    {
        var original = "Отказано в доступе";

        var decoded = ConsoleOutputDecoder.DecodeLine(Encoding.GetEncoding(1251).GetBytes(original));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeLine_Cp866Bytes_PreferCyrillicOverMojiBake()
    {
        // Байты «Привет» в CP866 при неверной трактовке как CP1251 дают
        // «ЏаЁўҐв» — меньше кириллицы, поэтому CP866 должен победить.
        var bytes = new byte[] { 0x8F, 0xE0, 0xA8, 0xA2, 0xA5, 0xE2 };

        Assert.Equal("Привет", ConsoleOutputDecoder.DecodeLine(bytes));
    }

    [Fact]
    public void DecodeLine_MixedAsciiAndOem_DecodedAsWhole()
    {
        var bytes = Encoding.GetEncoding(866).GetBytes("Ошибка: 5");

        Assert.Equal("Ошибка: 5", ConsoleOutputDecoder.DecodeLine(bytes));
    }

    [Fact]
    public async Task ReadLinesAsync_SplitsOnCrLfAndLf()
    {
        var payload = "line1\r\nline2\nline3";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(payload));
        var lines = new List<string>();

        await ConsoleOutputDecoder.ReadLinesAsync(stream, lines.Add, CancellationToken.None);

        Assert.Equal(new[] { "line1", "line2", "line3" }, lines);
    }

    [Fact]
    public async Task ReadLinesAsync_EmitsLastLineWithoutTrailingNewline()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("only"));
        var lines = new List<string>();

        await ConsoleOutputDecoder.ReadLinesAsync(stream, lines.Add, CancellationToken.None);

        var line = Assert.Single(lines);
        Assert.Equal("only", line);
    }

    [Fact]
    public async Task ReadLinesAsync_EmptyStream_ProducesNoLines()
    {
        using var stream = new MemoryStream();
        var lines = new List<string>();

        await ConsoleOutputDecoder.ReadLinesAsync(stream, lines.Add, CancellationToken.None);

        Assert.Empty(lines);
    }

    [Fact]
    public async Task ReadLinesAsync_SwallowsStandaloneCr_InsideLine()
    {
        // Одиночный \r внутри строки проглатывается: разрезание идёт только по \n,
        // поэтому "a\rb\r\nc" даёт ["ab", "c"].
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("a\rb\r\nc"));
        var lines = new List<string>();

        await ConsoleOutputDecoder.ReadLinesAsync(stream, lines.Add, CancellationToken.None);

        Assert.Equal(new[] { "ab", "c" }, lines);
    }

    [Fact]
    public async Task ReadLinesAsync_CanceledToken_ThrowsOperationCanceled()
    {
        using var stream = new MemoryStream(new byte[] { (byte)'a', (byte)'\n' });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ConsoleOutputDecoder.ReadLinesAsync(
                stream, _ => { }, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ReadLinesAsync_CyrillicPayload_DecodedLines()
    {
        using var stream = new MemoryStream(Encoding.GetEncoding(866).GetBytes("Ошибка\r\nГотово"));
        var lines = new List<string>();

        await ConsoleOutputDecoder.ReadLinesAsync(stream, lines.Add, CancellationToken.None);

        Assert.Equal(new[] { "Ошибка", "Готово" }, lines);
    }
}
