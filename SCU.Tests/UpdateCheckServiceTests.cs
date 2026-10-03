using Xunit;

namespace SCU.Tests;

// Разбор тегов GitHub Releases: префикс «v», суффиксы сборки/пререлиза.
public sealed class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("v2.3.1", "2.3.1")]
    [InlineData("2.3.1", "2.3.1")]
    [InlineData("V3.0", "3.0.0")]
    [InlineData("2.3", "2.3.0")]
    [InlineData("2.4.0-beta1", "2.4.0")]
    [InlineData("2.5.0+build.42", "2.5.0")]
    public void NormalizeVersion_ParsesTags(string tag, string expected)
    {
        var version = UpdateCheckService.NormalizeVersion(tag);

        Assert.NotNull(version);
        Assert.Equal(Version.Parse(expected), version);
    }

    [Fact]
    public void NormalizeVersion_TwoPartTag_EqualsThreePartTag()
    {
        // «2.3» и «2.3.0» — одна и та же версия: без нормализации до трёх
        // частей System.Version считал бы «2.3» более старой.
        Assert.Equal(
            UpdateCheckService.NormalizeVersion("2.3.0"),
            UpdateCheckService.NormalizeVersion("2.3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release")]
    [InlineData("v.next")]
    public void NormalizeVersion_InvalidTag_ReturnsNull(string? tag) =>
        Assert.Null(UpdateCheckService.NormalizeVersion(tag));

    [Fact]
    public void SelectVersionTag_SkipsDatabaseReleases()
    {
        // Latest-релизом бывает датный релиз базы (db-*): версией приложения
        // считается первый тег, разбираемый как версия.
        var tags = new[] { "db-2026.10.01", "v3.1.1", "v2.2.0" };

        Assert.Equal("v3.1.1", UpdateCheckService.SelectVersionTag(tags));
    }

    [Fact]
    public void SelectVersionTag_FirstParseableTag_Wins()
    {
        Assert.Equal("v3.2.0-beta1", UpdateCheckService.SelectVersionTag(new[] { "v3.2.0-beta1", "v3.1.1" }));
        Assert.Equal("v3.1.1", UpdateCheckService.SelectVersionTag(new[] { "v3.1.1" }));
    }

    [Fact]
    public void SelectVersionTag_NothingParseable_ReturnsNull()
    {
        Assert.Null(UpdateCheckService.SelectVersionTag(Array.Empty<string?>()));
        Assert.Null(UpdateCheckService.SelectVersionTag(new string?[] { "db-2026.10.01", "release", null, "" }));
    }

    [Fact]
    public void CurrentVersion_ReportsThreePartVersion() =>
        Assert.Matches(@"^\d+\.\d+\.\d+$", new UpdateCheckService().CurrentVersion);
}
