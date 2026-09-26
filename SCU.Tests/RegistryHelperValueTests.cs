using Microsoft.Win32;
using SCU.Services;
using Xunit;

namespace SCU.Tests;

// Регрессионные тесты на исправленные дефекты резерва реестра:
// REG_MULTI_SZ раньше кодировался как "System.String[]", а нечитаемый DWord
// молча превращался в 0 при Restore.
public class RegistryHelperValueTests
{
    [Fact]
    public void EncodeValue_MultiString_SerializesJsonArray()
    {
        // Новый формат: JSON-массив — сохраняет элементы, содержащие перевод строки.
        Assert.Equal("""["a","b","c"]""", RegistryHelper.EncodeValue(new[] { "a", "b", "c" }));
    }

    [Fact]
    public void MultiString_RoundTrip_PreservesEntries()
    {
        var original = new[] { @"C:\pagefile.sys 4096 4096", "D:\\page.sys 0 0" };

        var decoded = RegistryHelper.DecodeValue(
            RegistryValueKind.MultiString, RegistryHelper.EncodeValue(original));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void MultiString_RoundTrip_PreservesEmbeddedNewlines()
    {
        var original = new[] { "строка с\nпереводом", "обычная" };

        var decoded = RegistryHelper.DecodeValue(
            RegistryValueKind.MultiString, RegistryHelper.EncodeValue(original));

        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData("a\nb\nc")]
    [InlineData("a")]
    [InlineData("не json [вообще")]
    public void DecodeValue_MultiString_LegacyNewlineFormat_StillReadable(string legacyData)
    {
        // Резервы старого формата (Join("\n")) остаются читаемыми.
        var decoded = RegistryHelper.DecodeValue(RegistryValueKind.MultiString, legacyData);

        var expected = legacyData.Split('\n');
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void DecodeValue_CorruptDWord_ReturnsNull_NotZero()
    {
        // Повреждённый резерв не должен молча записывать 0 в живое значение реестра.
        Assert.Null(RegistryHelper.DecodeValue(RegistryValueKind.DWord, "не число"));
        Assert.Null(RegistryHelper.DecodeValue(RegistryValueKind.QWord, "не число"));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2147483647", 2147483647)]
    public void DecodeValue_ValidDWord_ParsesValue(string data, int expected)
    {
        Assert.Equal(expected, RegistryHelper.DecodeValue(RegistryValueKind.DWord, data));
    }

    [Fact]
    public void DecodeValue_UnsignedDWord_MaxUint_PreservesBits()
    {
        // 0xFFFFFFFF хранится в резерве как "4294967295"; при Restore биты восстанавливаются.
        var value = RegistryHelper.DecodeValue(RegistryValueKind.DWord, "4294967295");

        Assert.Equal(unchecked((int)0xFFFFFFFF), value);
    }

    [Fact]
    public void String_RoundTrip_PreservesText()
    {
        const string original = "значение с %переменными%";

        Assert.Equal(original, RegistryHelper.DecodeValue(
            RegistryValueKind.String, RegistryHelper.EncodeValue(original)));
    }

    [Fact]
    public void Binary_RoundTrip_PreservesBytes()
    {
        var original = new byte[] { 0x01, 0xFE, 0x00, 0xAB };

        var decoded = RegistryHelper.DecodeValue(
            RegistryValueKind.Binary, RegistryHelper.EncodeValue(original));

        Assert.Equal(original, decoded);
    }
}
