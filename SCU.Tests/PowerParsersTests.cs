using Xunit;

namespace SCU.Tests;

// П. 13 аудита: чистые парсеры PowerParsers покрываются тестами.
public class PowerParsersTests
{
    [Theory]
    [InlineData("ACPI\\ThermalZone\\TZ00_0: 27,9 C", "27,9°")]
    [InlineData("ACPI\\ThermalZone\\TZ00_0: 45 C", "45°")]
    [InlineData("no-colon 12,5 C", "12,5°")]
    public void FormatThermalZone_ParsesFirstZone(string raw, string expected)
    {
        Assert.Equal(expected, PowerParsers.FormatThermalZoneTemperature(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(датчики температуры не найдены)")]
    public void FormatThermalZone_NoData_ReturnsNull(string raw)
    {
        Assert.Null(PowerParsers.FormatThermalZoneTemperature(raw));
    }

    [Fact]
    public void ExtractBcdValue_ReturnsLastToken()
    {
        const string output = "Windows Boot Loader\r\n-------------------\r\nnumproc             8\r\ntruncatememory      0x10000000\r\n";
        Assert.Equal("8", PowerParsers.ExtractBcdValue(output, "numproc"));
        Assert.Equal("0x10000000", PowerParsers.ExtractBcdValue(output, "truncatememory"));
    }

    [Fact]
    public void ExtractBcdValue_Missing_ReturnsNull()
    {
        const string output = "identifier              {current}\r\ndevice                  partition=C:\r\n";
        Assert.Null(PowerParsers.ExtractBcdValue(output, "numproc"));
    }

    [Fact]
    public void ContainsBcdValue_DetectsValueName()
    {
        const string output = "numproc 8\r\nsafeboot Minimal\r\n";
        Assert.True(PowerParsers.ContainsBcdValue(output, "numproc"));
        Assert.True(PowerParsers.ContainsBcdValue(output, "safeboot"));
        Assert.False(PowerParsers.ContainsBcdValue(output, "truncatememory"));
    }

    [Theory]
    [InlineData("The volume state is: 0 (enabled)", true)]
    [InlineData("The volume state is: 1 (disabled)", false)]
    [InlineData("Состояние тома: 1 (отключено)", false)]
    [InlineData("8dot3 name creation is enabled", true)]
    [InlineData("8dot3 name creation is disabled", false)]
    public void TryParseVolumeState_ParsesKnownFormats(string text, bool expected)
    {
        Assert.Equal(expected, PowerParsers.TryParseVolumeStateEnabled(text));
    }

    [Fact]
    public void TryParseVolumeState_Unknown_ReturnsNull()
    {
        Assert.Null(PowerParsers.TryParseVolumeStateEnabled("совсем другой вывод"));
    }

    [Theory]
    [InlineData("DisableLastAccess = 1", 1)]
    [InlineData("DisableLastAccess = 0", 0)]
    [InlineData("какой-то заголовок\r\nDisableLastAccess = 2\r\nхвост", 2)]
    public void TryParseFsutilValue_ParsesNumber(string text, int expected)
    {
        Assert.Equal(expected, PowerParsers.TryParseFsutilValue(text));
    }

    [Fact]
    public void TryParseFsutilValue_NoNumber_ReturnsNull()
    {
        Assert.Null(PowerParsers.TryParseFsutilValue("нет цифр"));
    }
}
