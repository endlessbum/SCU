using SCU.Infrastructure.DeepSeek;
using Xunit;

namespace SCU.Tests;

// Аудит robustness: парсер списка моделей не бросает исключений на
// нестандартном ответе (изменение схемы, прокси, HTML-тело с HTTP 200).
public sealed class DeepSeekModelParsingTests
{
    private static IReadOnlyList<string> Parse(string json) =>
        DeepSeekClient.ParseModelIds(json, _ => true);

    [Fact]
    public void NormalResponse_ParsesIds()
    {
        var json = """{"data":[{"id":"deepseek-v4-flash"},{"id":"deepseek-v4-pro"}]}""";

        Assert.Equal(["deepseek-v4-flash", "deepseek-v4-pro"], Parse(json));
    }

    [Fact]
    public void MissingData_ReturnsEmpty()
    {
        // Раньше GetProperty("data") бросал KeyNotFoundException мимо catch(JsonException).
        Assert.Empty(Parse("""{"object":"list"}"""));
    }

    [Fact]
    public void DataNotArray_ReturnsEmpty()
    {
        Assert.Empty(Parse("""{"data":{"id":"x"}}"""));
        Assert.Empty(Parse("""{"data":null}"""));
    }

    [Fact]
    public void HtmlErrorBody_ReturnsEmpty()
    {
        Assert.Empty(Parse("<html><body>502 Bad Gateway</body></html>"));
    }

    [Fact]
    public void NonObjectEntriesAndNonStringIds_Skipped()
    {
        var json = """{"data":["string-entry",{"id":42},{"id":"deepseek-v4-flash"},{}]}""";

        Assert.Equal(["deepseek-v4-flash"], Parse(json));
    }
}
