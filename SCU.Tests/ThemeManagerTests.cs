using SCU.Common;
using Xunit;

namespace SCU.Tests;

public class ThemeManagerTests
{
    // Auto зависит от реестра системы, в тестах не проверяется.
    [Theory]
    [InlineData(AppTheme.Dark, true)]
    [InlineData(AppTheme.Light, false)]
    public void IsDarkTheme_ExplicitMode_ReturnsModeValue(AppTheme mode, bool expected)
    {
        Assert.Equal(expected, ThemeManager.IsDarkTheme(mode));
    }
}
