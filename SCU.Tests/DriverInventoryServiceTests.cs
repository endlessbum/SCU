using Xunit;
using SCU.Infrastructure.Windows.Drivers;

namespace SCU.Tests;

// Разбор DMTF-дат Win32_PnPSignedDriver и фильтр значимых классов устройств.
public class DriverInventoryServiceTests
{
    [Theory]
    [InlineData("20240214000000.000000-000", "2024-02-14")]
    [InlineData("20240214", "2024-02-14")]
    public void ParseCimDate_ParsesDatePrefix(string raw, string expected)
    {
        var parsed = DriverInventoryService.ParseCimDate(raw);

        Assert.NotNull(parsed);
        Assert.Equal(DateTime.Parse(expected), parsed.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2024")]
    [InlineData("not-a-date")]
    public void ParseCimDate_InvalidInputReturnsNull(string? raw)
    {
        Assert.Null(DriverInventoryService.ParseCimDate(raw));
    }

    [Theory]
    [InlineData("DISPLAY", true)]
    [InlineData("net", true)]
    [InlineData("SCSIAdapter", true)]
    [InlineData("Volume", false)]
    [InlineData("Mouse", false)]
    public void SignificantClasses_CoversKeyHardware(string deviceClass, bool expected)
    {
        Assert.Equal(expected, DriverInventoryService.SignificantClasses.Contains(deviceClass));
    }
}
