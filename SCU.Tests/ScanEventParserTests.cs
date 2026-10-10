using SCU.Interop;
using SCU.Models.Scan;
using Xunit;

namespace SCU.Tests;

// Парсер построчных JSON-событий ScannerCore.exe. Примеры строк — реальный
// формат протокола (см. SecurityScanner/src/ipc/event_writer.cpp).
public class ScanEventParserTests
{
    [Fact]
    public void Parse_NotJson_ReturnsUnknown()
    {
        var scanEvent = ScanEventParser.Parse("Some CRT garbage line");
        Assert.Equal(ScanEventKind.Unknown, scanEvent.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyLine_ReturnsUnknown(string line)
    {
        Assert.Equal(ScanEventKind.Unknown, ScanEventParser.Parse(line).Kind);
    }

    [Fact]
    public void Parse_Started_ReadsVersionAndMode()
    {
        var scanEvent = ScanEventParser.Parse(
            """{"event":"started","engineVersion":"0.1.0","mode":"custom","dbVersion":"2026.09.25","dbDate":"2026-09-25"}""");

        Assert.Equal(ScanEventKind.Started, scanEvent.Kind);
        Assert.Equal("0.1.0", scanEvent.EngineVersion);
        Assert.Equal("custom", scanEvent.Mode);
        Assert.Equal("2026.09.25", scanEvent.DbVersion);
        Assert.Equal("2026-09-25", scanEvent.DbDate);
    }

    [Fact]
    public void Parse_UpdateEvent_ReadsStatusAndEntries()
    {
        var ok = ScanEventParser.Parse(
            """{"event":"update","status":"ok","error":"","dbVersion":"2026.09.25","entries":42}""");
        Assert.Equal(ScanEventKind.Update, ok.Kind);
        Assert.Equal("ok", ok.UpdateStatus);
        Assert.Equal("2026.09.25", ok.DbVersion);
        Assert.Equal(42, ok.UpdateEntries);

        var failed = ScanEventParser.Parse(
            """{"event":"update","status":"failed","error":"signature verification failed","dbVersion":"","entries":0}""");
        Assert.Equal("failed", failed.UpdateStatus);
        Assert.Equal("signature verification failed", failed.Message);
    }

    [Fact]
    public void Parse_Progress_ReadsCounters()
    {
        var scanEvent = ScanEventParser.Parse(
            """{"event":"progress","scanned":12,"skipped":2,"detections":1,"current":"C:\\путь\\файл.exe"}""");

        Assert.Equal(ScanEventKind.Progress, scanEvent.Kind);
        Assert.Equal(12, scanEvent.Scanned);
        Assert.Equal(2, scanEvent.Skipped);
        Assert.Equal(1, scanEvent.Detections);
        Assert.Equal("C:\\путь\\файл.exe", scanEvent.Current);
    }

    [Fact]
    public void Parse_Detection_ReadsAllFields()
    {
        var scanEvent = ScanEventParser.Parse(
            """{"event":"detection","path":"C:\\x\\eicar.com","sha256":"275a021b","verdict":"malware","ruleId":"HASH-DB","description":"EICAR-Test-File","signedFile":false,"publisher":"","score":0,"containerPath":"","isVirtual":false,"source":"persistence","signals":[]}""");

        Assert.Equal(ScanEventKind.Detection, scanEvent.Kind);
        var detection = scanEvent.Detection;
        Assert.NotNull(detection);
        Assert.Equal(ScanVerdict.Malware, ParseVerdict(detection!.Verdict));
        Assert.Equal("HASH-DB", detection.RuleId);
        Assert.Equal("C:\\x\\eicar.com", detection.Path);
        Assert.Equal("persistence", detection.Source);
    }

    // targetPath: реальный файловый target persistence-записи (аудит: путь
    // записи — псевдопуть HKCU\...\Run\X, изолировать нужно файл цели).
    [Fact]
    public void Parse_Detection_ReadsTargetPath()
    {
        var scanEvent = ScanEventParser.Parse(
            """{"event":"detection","path":"HKCU\\...\\Run\\evil","targetPath":"C:\\x\\evil.exe","sha256":"aa","verdict":"malware","ruleId":"PERSISTENCE","description":"d","signedFile":false,"publisher":"","score":5,"containerPath":"","isVirtual":false,"source":"persistence","signals":[]}""");

        var detection = scanEvent.Detection;
        Assert.NotNull(detection);
        Assert.Equal(@"C:\x\evil.exe", detection!.TargetPath);
        Assert.True(detection.HasFileTarget);
        Assert.Equal(@"C:\x\evil.exe", detection.QuarantineTarget);
    }

    // persistence без targetPath (цель не существовала на момент скана):
    // файловых действий нет — кнопки «Просмотр»/«Карантин» скрываются.
    [Fact]
    public void Parse_Detection_PersistenceWithoutTarget_HasNoFileTarget()
    {
        var scanEvent = ScanEventParser.Parse(
            """{"event":"detection","path":"HKCU\\...\\Run\\x","targetPath":"","sha256":"","verdict":"suspicious","ruleId":"PERSISTENCE","description":"d","signedFile":false,"publisher":"","score":2,"containerPath":"","isVirtual":false,"source":"persistence","signals":[]}""");

        var detection = scanEvent.Detection;
        Assert.NotNull(detection);
        Assert.False(detection!.HasFileTarget);
    }

    // У file-детекции file target есть всегда, даже при пустом targetPath.
    [Fact]
    public void Parse_Detection_FileSource_AlwaysHasFileTarget()
    {
        var scanEvent = ScanEventParser.Parse(
            """{"event":"detection","path":"C:\\x\\e.exe","targetPath":"","sha256":"aa","verdict":"malware","ruleId":"HASH-DB","description":"d","signedFile":false,"publisher":"","score":0,"containerPath":"","isVirtual":false,"source":"file","signals":[]}""");

        var detection = scanEvent.Detection;
        Assert.NotNull(detection);
        Assert.True(detection!.HasFileTarget);
    }

    [Fact]
    public void Parse_Finished_BuildsResultWithSummary()
    {
        var scanEvent = ScanEventParser.Parse(
            """{"event":"finished","cancelled":false,"summary":{"filesScanned":115,"filesSkipped":0,"errors":0,"detections":0},"detections":[]}""");

        Assert.Equal(ScanEventKind.Finished, scanEvent.Kind);
        var result = scanEvent.Result;
        Assert.NotNull(result);
        Assert.False(result!.Cancelled);
        Assert.Equal(115, result.Summary.FilesScanned);
        Assert.Equal(0, result.Summary.Detections);
        Assert.Empty(result.Detections);
    }

    [Fact]
    public void Parse_FutureEventKind_IsCompatibleUnknown()
    {
        // Вперед-совместимость: новые события новых версий ScannerCore не роняют GUI.
        var scanEvent = ScanEventParser.Parse("""{"event":"quarantined","path":"C:\\x"}""");
        Assert.Equal(ScanEventKind.Unknown, scanEvent.Kind);
    }

    // ===================== Валидация summary (аудит 3, п. 8) =====================

    // {"summary":{}} без числовых полей — malformed, а не «чистый скан»:
    // все нули раньше классифицировались как Clean.
    [Fact]
    public void Parse_FinishedWithEmptySummaryObject_ResultIsNull()
    {
        var scanEvent = ScanEventParser.Parse("""{"event":"finished","cancelled":false,"summary":{},"detections":[]}""");

        Assert.Equal(ScanEventKind.Finished, scanEvent.Kind);
        Assert.Null(scanEvent.Result);
    }

    // {"summary":null} — раньше давал Summary == null и NRE в маппере.
    [Fact]
    public void Parse_FinishedWithNullSummary_ResultIsNull()
    {
        var scanEvent = ScanEventParser.Parse("""{"event":"finished","cancelled":false,"summary":null,"detections":[]}""");

        Assert.Equal(ScanEventKind.Finished, scanEvent.Kind);
        Assert.Null(scanEvent.Result);
    }

    // Частичный payload (не все поля) — тоже malformed.
    [Theory]
    [InlineData("""{"event":"finished","cancelled":false,"summary":{"filesScanned":5},"detections":[]}""")]
    [InlineData("""{"event":"finished","cancelled":false,"summary":{"filesScanned":5,"filesSkipped":0,"errors":"x","detections":0},"detections":[]}""")]
    public void Parse_FinishedWithMalformedSummary_ResultIsNull(string line)
    {
        var scanEvent = ScanEventParser.Parse(line);

        Assert.Equal(ScanEventKind.Finished, scanEvent.Kind);
        Assert.Null(scanEvent.Result);
    }

    private static ScanVerdict ParseVerdict(string verdict) => verdict switch
    {
        "clean" => ScanVerdict.Clean,
        "suspicious" => ScanVerdict.Suspicious,
        "malware" => ScanVerdict.Malware,
        "error" => ScanVerdict.Error,
        _ => ScanVerdict.Error,
    };
}
