using SCU.Infrastructure.Browser;
using Xunit;

namespace SCU.Tests;

// Разбор Content-Disposition при загрузках (аудит, минорная находка):
// ext-value с непустым language-part (RFC 5987) раньше не распознавался,
// и серверное имя терялось в пользу fallback из URL.
public class ContentDispositionFileNameTests
{
    [Theory]
    [InlineData("attachment; filename*=UTF-8''report.pdf", "report.pdf")]
    [InlineData("attachment; filename*=UTF-8'en'report.pdf", "report.pdf")]
    [InlineData("attachment; filename*=UTF-8'ru'%D0%BE%D1%82%D1%87%D0%B5%D1%82.pdf", "отчет.pdf")]
    [InlineData("attachment; filename=\"plain.txt\"", "plain.txt")]
    [InlineData("attachment; filename=plain.txt", "plain.txt")]
    public void FileName_Forms_AreRecognized(string header, string expected)
    {
        Assert.Equal(expected, BrowserTabHost.FileNameFromContentDisposition(header));
    }

    [Fact]
    public void FileName_WithoutKnownForm_ReturnsNull()
    {
        Assert.Null(BrowserTabHost.FileNameFromContentDisposition("attachment"));
    }
}
