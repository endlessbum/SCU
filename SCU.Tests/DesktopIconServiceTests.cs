using SCU.Common;
using Xunit;

namespace SCU.Tests;

// Имена окрашенных иконок для ярлыков (Assets\Icons): формат «SCU-<пресет>-light.ico».
// Вариант один для обеих тем (тёмные файлы убраны). Файлы генерируются
// tools/AccentIconGenerator, поэтому формат должен совпадать с ним.
public sealed class DesktopIconServiceTests
{
    [Theory]
    [InlineData(AppAccent.Blue, "SCU-blue-light.ico")]
    [InlineData(AppAccent.SkyBlue, "SCU-skyblue-light.ico")]
    [InlineData(AppAccent.Purple, "SCU-purple-light.ico")]
    [InlineData(AppAccent.Green, "SCU-green-light.ico")]
    [InlineData(AppAccent.Orange, "SCU-orange-light.ico")]
    public void GetIconFileName_MapsPreset(AppAccent accent, string expected)
    {
        Assert.Equal(expected, DesktopIconService.GetIconFileName(accent));
    }
}
