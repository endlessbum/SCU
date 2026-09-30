using Xunit;

namespace SCU.Tests;

// Разбор тегов GitHub Releases: префикс «v», суффиксы сборки/пререлиза.
public sealed class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("v2.3.1", "2.3.1")]
    [InlineData("2.3.1", "2.3.1")]
    [InlineData("V3.0", "3.0")]
    [InlineData("2.4.0-beta1", "2.4.0")]
    [InlineData("2.5.0+build.42", "2.5.0")]
    public void NormalizeVersion_ParsesTags(string tag, string expected)
    {
        var version = UpdateCheckService.NormalizeVersion(tag);

        Assert.NotNull(version);
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release")]
    [InlineData("v.next")]
    public void NormalizeVersion_InvalidTag_ReturnsNull(string? tag) =>
        Assert.Null(UpdateCheckService.NormalizeVersion(tag));

    [Fact]
    public void CurrentVersion_ReportsThreePartVersion() =>
        Assert.Matches(@"^\d+\.\d+\.\d+$", new UpdateCheckService().CurrentVersion);
}
