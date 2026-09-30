using Xunit;

namespace SCU.Tests;

// Политика навигации встроенного браузера: http/https разрешены, опасные
// и нативные схемы блокируются, внешние протоколы — только с подтверждением.
public sealed class BrowserNavigationPolicyTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/page?q=1")]
    public void Evaluate_HttpHttps_Allow(string uri) =>
        Assert.Equal(BrowserNavigationDecision.Allow, BrowserNavigationPolicy.Evaluate(uri));

    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox")]
    [InlineData("powershell:start calc")]
    [InlineData("cmd:/c calc")]
    [InlineData("shell:Downloads")]
    [InlineData("")]
    [InlineData("not a uri at all")]
    public void Evaluate_DangerousOrMalformed_Block(string uri) =>
        Assert.Equal(BrowserNavigationDecision.Block, BrowserNavigationPolicy.Evaluate(uri));

    [Theory]
    [InlineData("mailto:user@example.com")]
    [InlineData("tel:+70000000000")]
    [InlineData("ms-settings:display")]
    [InlineData("unknown-scheme:payload")]
    public void Evaluate_ExternalSchemes_RequireConfirmation(string uri) =>
        Assert.Equal(
            BrowserNavigationDecision.ExternalWithConfirmation,
            BrowserNavigationPolicy.Evaluate(uri));

    [Fact]
    public void Evaluate_NullInput_Block() =>
        Assert.Equal(BrowserNavigationDecision.Block, BrowserNavigationPolicy.Evaluate(null));

    [Fact]
    public void Evaluate_AboutBlank_Allowed_OtherAboutBlocked()
    {
        Assert.Equal(BrowserNavigationDecision.Allow, BrowserNavigationPolicy.Evaluate("about:blank"));
        Assert.Equal(BrowserNavigationDecision.Block, BrowserNavigationPolicy.Evaluate("about:config"));
    }

    // data:-навигация — атакер-контент на «пустом» адресе: блокируется целиком,
    // включая верхний уровень (стартовая страница обслуживается virtual host'ом).
    [Theory]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("about:data")]
    public void Evaluate_DataAndUnknownAbout_Block(string uri) =>
        Assert.Equal(BrowserNavigationDecision.Block, BrowserNavigationPolicy.Evaluate(uri));

    // ===================== Адресная строка =====================

    [Theory]
    [InlineData("hello world")]
    [InlineData("webview2 wpf")]
    public void ResolveAddress_SearchPhrase_BuildsGoogleSearchUrl(string query)
    {
        var url = BrowserNavigationPolicy.ResolveAddress(query);
        Assert.StartsWith("https://www.google.com/search?q=", url);
        Assert.Contains(Uri.EscapeDataString(query), url);
    }

    [Fact]
    public void ResolveAddress_UrlWithScheme_KeptAsIs()
    {
        var url = BrowserNavigationPolicy.ResolveAddress("https://example.com/x?y=1");
        Assert.Equal("https://example.com/x?y=1", url);
    }

    [Fact]
    public void ResolveAddress_DomainWithoutScheme_TreatedAsHost()
    {
        var url = BrowserNavigationPolicy.ResolveAddress("example.com");
        Assert.Equal("https://example.com/", url);
    }

    [Fact]
    public void ResolveAddress_Empty_ReturnsEmpty() =>
        Assert.Equal(string.Empty, BrowserNavigationPolicy.ResolveAddress("   "));
}
