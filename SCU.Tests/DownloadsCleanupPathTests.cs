using SCU.Infrastructure.Windows.Maintenance;
using Xunit;

namespace SCU.Tests;

// Карточка «Путь очистки загрузок»: дополнительная папка очищается вместе со
// стандартными «Загрузками», но совпадающий или вложенный путь не должен
// попадать в очистку дважды.
public sealed class DownloadsCleanupPathTests
{
    [Theory]
    [InlineData(@"C:\Users\user\Downloads", @"C:\Users\user\Downloads")]
    [InlineData(@"C:\Users\user\Downloads", @"c:\users\user\downloads")] // регистр
    [InlineData(@"C:\Users\user\Downloads", @"C:\Users\user\Downloads\")] // хвостовой слэш
    [InlineData(@"C:\Users\user\Downloads", @"C:\Users\user\Downloads\Torrents")] // вложена в «Загрузки»
    public void DuplicateOrNestedInsideStandard_IsDuplicate(string standard, string custom)
    {
        Assert.True(FileCleanupService.IsDownloadsPathDuplicate(standard, custom));
    }

    [Theory]
    [InlineData(@"C:\Users\user\Downloads", @"D:\Downloads")]
    [InlineData(@"C:\Users\user\Downloads", @"C:\Users\user\DownloadsOld")]
    [InlineData(@"C:\Users\user\Downloads", @"C:\Users\other\Downloads")]
    // «Загрузки» вложены в пользовательскую папку: папка чистится дополнительно,
    // её содержимое вне «Загрузок» пользователь тоже просил удалить.
    [InlineData(@"C:\Users\user\Downloads", @"C:\Users\user")]
    public void DistinctPath_IsNotDuplicate(string standard, string custom)
    {
        Assert.False(FileCleanupService.IsDownloadsPathDuplicate(standard, custom));
    }
}
