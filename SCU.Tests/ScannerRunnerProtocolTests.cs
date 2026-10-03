using SCU.Interop;
using SCU.Models.Scan;
using Xunit;

namespace SCU.Tests;

// Регрессии протокола ScannerCore ↔ Runner (аудит 3, п. 2):
// finished без payload результата — протокольная ошибка, а не «чистый скан».
public class ScannerRunnerProtocolTests
{
    private static readonly SCU.Infrastructure.Logging.Logger Logger =
        SCU.Infrastructure.Logging.Logger.CreateForCurrentRun();

    private static ScanEvent ParseLine(string line) => ScanEventParser.Parse(line);

    [Fact]
    public void Parse_FinishedWithoutSummary_ResultIsNull()
    {
        // {"event":"finished","cancelled":false} — обрыв протокола: раньше
        // десериализовался в пустой ScanResultDto и считался Clean.
        var scanEvent = ParseLine("""{"event":"finished","cancelled":false}""");

        Assert.Equal(ScanEventKind.Finished, scanEvent.Kind);
        Assert.Null(scanEvent.Result);
    }

    [Fact]
    public void Parse_FinishedWithSummary_ResultParsed()
    {
        var scanEvent = ParseLine(
            """{"event":"finished","cancelled":false,"summary":{"filesScanned":5,"filesSkipped":1,"errors":0,"detections":0},"detections":[]}""");

        Assert.Equal(ScanEventKind.Finished, scanEvent.Kind);
        Assert.NotNull(scanEvent.Result);
        Assert.Equal(5, scanEvent.Result!.Summary.FilesScanned);
    }

    [Fact]
    public void Runner_FinishedWithoutResult_IsProtocolError_NotClean()
    {
        var runner = new ScannerRunner(Logger);
        var context = new ScannerRunner.RunContext();

        runner.HandleEventLine(ParseLine("""{"event":"finished","cancelled":false}"""), context);

        Assert.Null(context.Result);
        Assert.True(context.HadProtocolError);
    }

    [Fact]
    public void Runner_FinishedWithResult_IsRecorded()
    {
        var runner = new ScannerRunner(Logger);
        var context = new ScannerRunner.RunContext();

        runner.HandleEventLine(ParseLine(
            """{"event":"finished","cancelled":false,"summary":{"filesScanned":3,"filesSkipped":0,"errors":0,"detections":0},"detections":[]}"""),
            context);

        Assert.NotNull(context.Result);
        Assert.False(context.HadProtocolError);
    }
}
