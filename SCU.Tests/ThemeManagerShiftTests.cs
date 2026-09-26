using System.Windows.Media;
using SCU.Common;
using Xunit;

namespace SCU.Tests;

// Границы масштабирования акцентного цвета: Clamp обязан резать 0..255,
// Shift не должен переполнять байты при factor > 1.
public class ThemeManagerShiftTests
{
    [Theory]
    [InlineData(-10f, 0)]
    [InlineData(0f, 0)]
    [InlineData(127.4f, 127)]
    [InlineData(127.6f, 128)]
    [InlineData(255.4f, 255)]
    [InlineData(300f, 255)]
    public void Clamp_RoundsThenCutsToByteRange(float value, byte expected)
    {
        Assert.Equal(expected, ThemeManager.Clamp(value));
    }

    [Fact]
    public void Shift_FactorOne_ReturnsSameColor()
    {
        var color = Color.FromRgb(10, 130, 250);

        Assert.Equal(color, ThemeManager.Shift(color, 1f));
    }

    [Fact]
    public void Shift_FactorAboveOne_SaturatesWithoutWrapAround()
    {
        var shifted = ThemeManager.Shift(Color.FromRgb(200, 100, 50), 2f);

        Assert.Equal(255, shifted.R);
        Assert.Equal(200, shifted.G);
        Assert.Equal(100, shifted.B);
    }

    [Fact]
    public void Shift_FactorBelowOne_Darkens()
    {
        var shifted = ThemeManager.Shift(Color.FromRgb(100, 100, 100), 0.5f);

        Assert.Equal(50, shifted.R);
        Assert.Equal(50, shifted.G);
        Assert.Equal(50, shifted.B);
    }

    [Fact]
    public void Shift_BlackStaysBlack()
    {
        Assert.Equal(Colors.Black, ThemeManager.Shift(Colors.Black, 3f));
    }
}
