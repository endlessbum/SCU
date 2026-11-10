using Xunit;

namespace SCU.Tests;

// П. 13 аудита (B4) + SCU-01: чистые разборы вывода PowerShell-скриптов AdapterProfileManager.
// SCU-01: неполный backup (BACKUP_FAIL / недостающие адаптеры) обязан отвергаться,
// а не сохраняться как успешный.
public class AdapterProfileManagerTests
{
    [Fact]
    public void ParseBackupOutput_KeepsAdapterLines_AndTrims()
    {
        const string output = """
        ADAPTER|Ethernet|*RSS|1
        BACKUP_FAIL|WiFi|access denied
        ADAPTER|Ethernet|*EEE|-

        ADAPTER|Ethernet2 |*FlowControl| 3
        BACKUP_TOTAL|2|1
        """;

        var parse = AdapterProfileManager.ParseBackupOutput(output);

        Assert.Equal(3, parse.AdapterLines.Count);
        Assert.Equal("ADAPTER|Ethernet|*RSS|1", parse.AdapterLines[0]);
        Assert.Equal("ADAPTER|Ethernet|*EEE|-", parse.AdapterLines[1]);
        // TrimEntries убирает края строки целиком; внутренние пробелы сохраняются
        // (это поведение оригинала — имя адаптера не нормализуется).
        Assert.Equal("ADAPTER|Ethernet2 |*FlowControl| 3", parse.AdapterLines[2]);
        Assert.Equal(["WiFi"], parse.FailedAdapterNames);
        Assert.Equal(2, parse.ExpectedAdapters);
        Assert.Equal(1, parse.OkAdapters);
    }

    [Fact]
    public void ParseBackupOutput_EmptyAndNoAdapters()
    {
        var empty = AdapterProfileManager.ParseBackupOutput("");
        Assert.Empty(empty.AdapterLines);
        Assert.Equal(-1, empty.ExpectedAdapters);

        var noAdapters = AdapterProfileManager.ParseBackupOutput("NO_ADAPTERS");
        Assert.Equal(0, noAdapters.ExpectedAdapters);
        Assert.Equal(0, noAdapters.OkAdapters);
    }

    [Fact]
    public void ValidateBackupOutput_CompleteBackup_ReturnsNull()
    {
        var parse = AdapterProfileManager.ParseBackupOutput("""
        ADAPTER|Ethernet|*RSS|1
        ADAPTER|Ethernet|*EEE|-
        ADAPTER|Ethernet 2|*RSS|2
        BACKUP_TOTAL|2|2
        """);

        Assert.Null(AdapterProfileManager.ValidateBackupOutput(parse));
    }

    [Fact]
    public void ValidateBackupOutput_BackupFail_IsRejected()
    {
        // Раньше этот вывод сохранялся как успешный: фильтр ADAPTER| терял
        // BACKUP_FAIL, и для WiFi откат был бы невозможен (SCU-01).
        var parse = AdapterProfileManager.ParseBackupOutput("""
        ADAPTER|Ethernet|*RSS|1
        ADAPTER|Ethernet|*EEE|-
        BACKUP_FAIL|WiFi|access denied
        BACKUP_TOTAL|2|1
        """);

        var reason = AdapterProfileManager.ValidateBackupOutput(parse);

        Assert.NotNull(reason);
        Assert.Contains("WiFi", reason);
    }

    [Fact]
    public void ValidateBackupOutput_MissingAdapter_IsRejected()
    {
        var parse = AdapterProfileManager.ParseBackupOutput("""
        ADAPTER|Ethernet|*RSS|1
        BACKUP_TOTAL|2|2
        """);

        var reason = AdapterProfileManager.ValidateBackupOutput(parse);

        Assert.NotNull(reason);
        Assert.Contains("1 из 2", reason);
    }

    [Fact]
    public void ValidateBackupOutput_MissingTotal_IsRejected()
    {
        var parse = AdapterProfileManager.ParseBackupOutput("ADAPTER|Ethernet|*RSS|1");

        Assert.NotNull(AdapterProfileManager.ValidateBackupOutput(parse));
    }

    [Fact]
    public void ValidateBackupOutput_NoAdapters_IsNotIncompleteness()
    {
        var parse = AdapterProfileManager.ParseBackupOutput("NO_ADAPTERS");

        Assert.Null(AdapterProfileManager.ValidateBackupOutput(parse));
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

// OBS-03 (аудит 2026-11-04): приёмка существующего бэкапа по формату каждой строки —
// усечённый/битый файл не должен «узакониваться» наличием хотя бы одной строки ADAPTER|.
public class AdapterBackupLineValidationTests
{
    [Theory]
    [InlineData("ADAPTER|Ethernet|*RSS|1", true)]
    [InlineData("ADAPTER|Ethernet|*EEE|-", true)]
    [InlineData("ADAPTER|Ethernet|*RSS|", true)]              // пустое значение — допустимо (restore пропустит по $key/$value '-')
    [InlineData("ADAPTER|Ethernet|*RSS|1|2|3", true)]          // значение содержит '|' — split с limit 4
    [InlineData("ADAPTER|Ethernet|*RSS", false)]               // нет значения
    [InlineData("ADAPTER|Ethernet", false)]                    // нет keyword и значения
    [InlineData("ADAPTER||*RSS|1", false)]                     // пустое имя адаптера
    [InlineData("ADAPTER|Ethernet||1", false)]                 // пустой keyword
    [InlineData("BACKUP_TOTAL|2|2", false)]                    // чужой префикс
    [InlineData("ADAPTER_FAIL|Ethernet|boom", false)]
    [InlineData("", false)]                                    // пустая строка — не запись
    public void IsValidAdapterBackupLine_ChecksAllFourFields(string line, bool expected)
    {
        Assert.Equal(expected, AdapterProfileManager.IsValidAdapterBackupLine(line));
    }
}
