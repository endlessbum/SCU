using SCU.Common;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// Чистые форматтеры CleanupViewModel: сводка очистки и человекочитаемые размеры.
public class CleanupFormattingTests
{
    [Fact]
    public void FormatBytes_Megabytes_UsesInvariantOneDecimal()
    {
        Assert.Equal("1.5 MB", CleanupViewModel.FormatBytes((long)(1.5 * 1024 * 1024)));
        Assert.Equal("1.0 MB", CleanupViewModel.FormatBytes(1024 * 1024));
    }

    [Fact]
    public void FormatBytes_BelowMegabyte_ShowsByteCount()
    {
        // N0 c InvariantCulture ставит разделители групп запятыми.
        Assert.Equal("1,023 байт", CleanupViewModel.FormatBytes(1023));
        Assert.Equal("0 байт", CleanupViewModel.FormatBytes(0));
        Assert.Equal("1,048,575 байт", CleanupViewModel.FormatBytes(1024 * 1024 - 1));
    }

    [Fact]
    public void FormatCleanupResult_Failure_IncludesCodeAndMessage()
    {
        var result = Result<IReadOnlyList<CleanupItemResult>>.Failure("нет доступа", 7);

        var text = CleanupViewModel.FormatCleanupResult(result);

        Assert.Contains("7", text);
        Assert.Contains("нет доступа", text);
    }

    [Fact]
    public void FormatCleanupResult_EmptyList_SaysNothingToClean()
    {
        var result = Result<IReadOnlyList<CleanupItemResult>>.Success([]);

        Assert.Contains("Нечего очищать", CleanupViewModel.FormatCleanupResult(result));
    }

    [Fact]
    public void FormatCleanupResult_SumsFilesAndBytes()
    {
        Result<IReadOnlyList<CleanupItemResult>> result = Result<IReadOnlyList<CleanupItemResult>>.Success(
        [
            new CleanupItemResult("Temp", true, 10, 8, 2 * 1024 * 1024),
            new CleanupItemResult("Logs", true, 5, 2, 512),
        ]);

        var text = CleanupViewModel.FormatCleanupResult(result);

        Assert.Contains("10", text);              // 8 + 2 удалённых файла
        Assert.Contains("2.0 MB", text);          // 2*1024*1024 + 512 байт
        Assert.Contains("Осталось занятых: 5.", text); // (10-8) + (5-2)
    }

    [Fact]
    public void FormatCleanupResult_EverythingDeleted_HasNoRemainingPart()
    {
        Result<IReadOnlyList<CleanupItemResult>> result = Result<IReadOnlyList<CleanupItemResult>>.Success(
        [
            new CleanupItemResult("Temp", true, 4, 4, 100),
        ]);

        Assert.DoesNotContain("Осталось", CleanupViewModel.FormatCleanupResult(result));
    }
}
