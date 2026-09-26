using SCU.Services;
using Xunit;

namespace SCU.Tests;

// П. 13 аудита (B4): чистые разборы вывода PowerShell-скриптов AdapterProfileManager.
public class AdapterProfileManagerTests
{
    [Fact]
    public void SelectBackupLines_KeepsOnlyAdapterLines_AndTrims()
    {
        const string output = """
        ADAPTER|Ethernet|*RSS|1
        BACKUP_FAIL|WiFi|access denied
        ADAPTER|Ethernet|*EEE|-

        ADAPTER|Ethernet2 |*FlowControl| 3
        """;

        var lines = AdapterProfileManager.SelectBackupLines(output);

        Assert.Equal(3, lines.Count);
        Assert.Equal("ADAPTER|Ethernet|*RSS|1", lines[0]);
        Assert.Equal("ADAPTER|Ethernet|*EEE|-", lines[1]);
        // TrimEntries убирает края строки целиком; внутренние пробелы сохраняются
        // (это поведение оригинала — имя адаптера не нормализуется).
        Assert.Equal("ADAPTER|Ethernet2 |*FlowControl| 3", lines[2]);
    }

    [Fact]
    public void SelectBackupLines_EmptyOutput_ReturnsEmpty()
    {
        Assert.Empty(AdapterProfileManager.SelectBackupLines(""));
        Assert.Empty(AdapterProfileManager.SelectBackupLines("NO_ADAPTERS"));
    }

    [Theory]
    [InlineData("RESTORE|Ethernet|*RSS|OK\nRESTORE|Ethernet|*EEE|OK", 2, 0, 0)]
    [InlineData("RESTORE|Ethernet|*RSS|OK\nRESTORE|Ethernet|*EEE|FAIL|boom", 1, 1, 0)]
    [InlineData("RESTORE|Ethernet|*RSS|OK\nRESTART|FAIL|Ethernet", 1, 0, 1)]
    [InlineData("NO_BACKUP", 0, 0, 0)]
    [InlineData("", 0, 0, 0)]
    public void SummarizeRestoreOutput_CountsCategories(string output, int restored, int failures, int restartsFailed)
    {
        var (r, f, rf) = AdapterProfileManager.SummarizeRestoreOutput(output);

        Assert.Equal(restored, r);
        Assert.Equal(failures, f);
        Assert.Equal(restartsFailed, rf);
    }
}
