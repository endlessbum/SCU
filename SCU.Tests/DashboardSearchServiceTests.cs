using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// П. 13 аудита: поиск Dashboard (синонимы/матчинг) — чистый сервис.
public class DashboardSearchServiceTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("очистка temp", 2)]
    [InlineData("  multiple   spaces  query ", 3)]
    public void SplitTokens_SplitsAndTrims(string query, int expected)
    {
        Assert.Equal(expected, DashboardSearchService.SplitTokens(query).Length);
    }

    [Fact]
    public void MatchesToken_DirectMatch()
    {
        Assert.True(DashboardSearchService.MatchesToken("очист", "Очистка диска clean"));
        Assert.True(DashboardSearchService.MatchesToken("CLEAN", "Очистка диска clean"));
    }

    [Fact]
    public void MatchesToken_SynonymExpansion()
    {
        // «мусор» → стемы bloat/junk/trash — попадает в утилиту через синоним.
        Assert.True(DashboardSearchService.MatchesToken("мусор", "Bloat removal junk"));
        // Префиксное сопоставление: слово пользователя — начало ключа словаря.
        Assert.True(DashboardSearchService.MatchesToken("очистка", "clean temp files"));
    }

    [Fact]
    public void MatchesToken_NoMatch()
    {
        Assert.False(DashboardSearchService.MatchesToken("вебкамера", "Очистка диска clean"));
    }

    [Fact]
    public void MatchesText_EmptyTokens_MatchesEverything()
    {
        Assert.True(DashboardSearchService.MatchesText("что угодно", []));
    }

    [Fact]
    public void MatchesText_AllTokensRequired()
    {
        const string text = "Очистка temp cache";
        Assert.True(DashboardSearchService.MatchesText(text, ["очист", "temp"]));
        Assert.False(DashboardSearchService.MatchesText(text, ["очист", "wifi"]));
    }
}
