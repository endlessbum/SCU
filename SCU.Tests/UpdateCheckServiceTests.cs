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
    public void SelectLatestStableRelease_SkipsDatabaseReleases()
    {
        // Релизы базы (db-* / scanner-db) — не версии приложения.
        var releases = new[]
        {
            new UpdateCheckService.GitHubReleaseInfo("db-2026.10.01", false, false),
            new UpdateCheckService.GitHubReleaseInfo("scanner-db", false, false),
            new UpdateCheckService.GitHubReleaseInfo("v3.1.1", false, false),
            new UpdateCheckService.GitHubReleaseInfo("v2.2.0", false, false)
        };

        Assert.Equal("v3.1.1", UpdateCheckService.SelectLatestStableRelease(releases)?.TagName);
    }

    [Fact]
    public void SelectLatestStableRelease_PrereleaseBeforeStable_PicksStable()
    {
        // Ключевой регресс: prerelease, опубликованный позже stable, не должен
        // загораживать её — раньше выбирался первый подходящий тег списка.
        var releases = new[]
        {
            new UpdateCheckService.GitHubReleaseInfo("v3.2.0-beta1", false, true),
            new UpdateCheckService.GitHubReleaseInfo("v3.1.3", false, false),
            new UpdateCheckService.GitHubReleaseInfo("v3.1.2", false, false)
        };

        Assert.Equal("v3.1.3", UpdateCheckService.SelectLatestStableRelease(releases)?.TagName);
    }

    [Fact]
    public void SelectLatestStableRelease_MultipleStable_PicksMaximum()
    {
        // Порядок в списке GitHub — по дате, не по версии: берём максимум.
        var releases = new[]
        {
            new UpdateCheckService.GitHubReleaseInfo("v3.1.2", false, false),
            new UpdateCheckService.GitHubReleaseInfo("v3.1.10", false, false),
            new UpdateCheckService.GitHubReleaseInfo("v3.1.9", false, false)
        };

        Assert.Equal("v3.1.10", UpdateCheckService.SelectLatestStableRelease(releases)?.TagName);
    }

    [Fact]
    public void SelectLatestStableRelease_PrereleaseWithoutDash_Skipped()
    {
        // Статус prerelease берётся из метаданных релиза, а не из «-» в теге.
        var releases = new[]
        {
            new UpdateCheckService.GitHubReleaseInfo("v4.0.0", false, true),
            new UpdateCheckService.GitHubReleaseInfo("v3.1.3", false, false)
        };

        Assert.Equal("v3.1.3", UpdateCheckService.SelectLatestStableRelease(releases)?.TagName);
    }

    [Fact]
    public void SelectLatestStableRelease_Draft_Skipped()
    {
        var releases = new[]
        {
            new UpdateCheckService.GitHubReleaseInfo("v4.0.0", true, false),
            new UpdateCheckService.GitHubReleaseInfo("v3.1.3", false, false)
        };

        Assert.Equal("v3.1.3", UpdateCheckService.SelectLatestStableRelease(releases)?.TagName);
    }

    [Fact]
    public void SelectLatestStableRelease_OnlyPrereleases_ReturnsNull()
    {
        var releases = new[]
        {
            new UpdateCheckService.GitHubReleaseInfo("v3.2.0-beta1", false, true),
            new UpdateCheckService.GitHubReleaseInfo("db-2026.10.01", false, false),
            new UpdateCheckService.GitHubReleaseInfo("release", false, false)
        };

        Assert.Null(UpdateCheckService.SelectLatestStableRelease(releases));
    }

    [Fact]
    public void SelectLatestStableRelease_Empty_ReturnsNull() =>
        Assert.Null(UpdateCheckService.SelectLatestStableRelease(Array.Empty<UpdateCheckService.GitHubReleaseInfo>()));

    [Fact]
    public void CurrentVersion_ReportsThreePartVersion() =>
        Assert.Matches(@"^\d+\.\d+\.\d+$", new UpdateCheckService().CurrentVersion);
}
