using System.Text;
using SCU.Common;
using Xunit;

namespace SCU.Tests;

// Форматтеры безопасного просмотра: содержимое — только данные, никакой
// интерпретации и исполнения (документ п. 22).
public class SafeContentTests
{
    [Fact]
    public void IsLikelyText_PlainAscii_True()
    {
        var bytes = "hello world, this is a plain text file\n"u8.ToArray();
        Assert.True(SafeContent.IsLikelyText(bytes));
    }

    [Fact]
    public void IsLikelyText_Utf8Cyrillic_True()
    {
        var bytes = Encoding.UTF8.GetBytes("Проверка содержимого без нулевых байтов");
        Assert.True(SafeContent.IsLikelyText(bytes));
    }

    [Fact]
    public void IsLikelyText_NullBytes_False()
    {
        var bytes = new byte[] { 0x4D, 0x5A, 0x00, 0x90, 0x00, 0x03 }; // PE-заголовок
        Assert.False(SafeContent.IsLikelyText(bytes));
    }

    [Fact]
    public void IsLikelyText_Empty_True()
    {
        Assert.True(SafeContent.IsLikelyText(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ToHexDump_FormatWithOffsetAndAsciiColumn()
    {
        var bytes = new byte[17];
        bytes[0] = 0x48; // H
        bytes[1] = 0x69; // i
        bytes[16] = 0x21; // !

        var dump = SafeContent.ToHexDump(bytes);
        var lines = dump.Split('\n');

        Assert.StartsWith("00000000", lines[0]);
        Assert.Contains("48 69", lines[0]);
        Assert.Contains("Hi", lines[0]);
        // Вторая строка: один байт с дополнением пробелами.
        Assert.StartsWith("00000010", lines[1]);
        Assert.Contains("21", lines[1]);
        Assert.Contains("!", lines[1]);
    }

    [Fact]
    public void ToHexDump_NonPrintableShownAsDot()
    {
        var dump = SafeContent.ToHexDump(new byte[] { 0x00, 0xFF });
        Assert.Contains("..", dump);
    }

    [Fact]
    public void ExtractStrings_MinLengthRespected()
    {
        // "ab" (2 < minLength) и "hello_world" разделены NUL-байтом.
        var bytes = new byte[] { (byte)'a', (byte)'b', 0x00 }
            .Concat("hello_world"u8.ToArray()).ToArray();
        var strings = SafeContent.ExtractStrings(bytes);

        Assert.Single(strings);
        Assert.Equal("hello_world", strings[0]);
    }

    [Fact]
    public void ExtractStrings_SplitByNonPrintable()
    {
        var bytes = new byte[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x00, 0x58, 0x59, 0x5A, 0x31, 0x32 };
        var strings = SafeContent.ExtractStrings(bytes);

        Assert.Equal(2, strings.Count);
        Assert.Equal("ABCDE", strings[0]);
        Assert.Equal("XYZ12", strings[1]);
    }

    [Fact]
    public void DecodeText_Utf8Cyrillic()
    {
        var bytes = Encoding.UTF8.GetBytes("Содержимое файла");
        Assert.Equal("Содержимое файла", SafeContent.DecodeText(bytes));
    }

    [Fact]
    public void DecodeText_InvalidUtf8FallsBackToCp1251()
    {
        // "привет" в CP1251; в строгом UTF-8 эта последовательность невалидна
        // (0xF0 без продолжений, 0xE8/0xE2/0xE5/0xF2 — не продолжения).
        var bytes = new byte[] { 0xEF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2 };
        var decoded = SafeContent.DecodeText(bytes);
        Assert.Equal("привет", decoded);
    }
}
