using SCU.Models.Scan;
using Xunit;

namespace SCU.Tests;

// Матрица аудита (п. 14, приоритет 7): Finished + Summary → единый ScanOutcome.
// Проверяет контракт ScannerRunner: Partial никогда не попадает в ветку Clean.
public class ScanOutcomeMapperTests
{
    private static ScanResultDto MakeResult(
        bool cancelled = false,
        long scanned = 10,
        long skipped = 0,
        long errors = 0,
        int detections = 0)
    {
        var result = new ScanResultDto
        {
            Cancelled = cancelled,
            Summary = new ScanSummaryDto
            {
                FilesScanned = scanned,
                FilesSkipped = skipped,
                Errors = errors,
                Detections = detections,
            },
        };
        for (var i = 0; i < detections; i++)
        {
            result.Detections.Add(new DetectionDto { Path = $"f{i}.exe", Verdict = "malware" });
        }
        return result;
    }

    // Finished отсутствует (ScannerCore упал до отчёта) — Failed.
    [Fact]
    public void NoFinishedEvent_MapsToFailed()
    {
        Assert.Equal(ScanOutcome.Failed, ScanOutcomeMapper.Map(null));
    }

    // finished.cancelled = true — Cancelled независимо от счётчиков.
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 0, 0)]
    [InlineData(0, 3, 2)]
    public void CancelledFinished_MapsToCancelled(long skipped, long errors, int detections)
    {
        var result = MakeResult(cancelled: true, skipped: skipped, errors: errors, detections: detections);
        Assert.Equal(ScanOutcome.Cancelled, ScanOutcomeMapper.Map(result));
    }

    // Ошибки сканирования — Partial, даже если есть детекты.
    [Fact]
    public void ErrorsOverZero_MapsToPartial()
    {
        var result = MakeResult(errors: 2);
        Assert.Equal(ScanOutcome.Partial, ScanOutcomeMapper.Map(result));
    }

    // Пропуски (архивы, размер, нечитаемость) — Partial без ошибок.
    [Fact]
    public void SkippedOverZero_MapsToPartial()
    {
        var result = MakeResult(skipped: 7);
        Assert.Equal(ScanOutcome.Partial, ScanOutcomeMapper.Map(result));
    }

    // Ошибки + пропуски + детекты одновременно — Partial (не Threats, не Clean).
    [Fact]
    public void ErrorsAndSkippedAndDetections_MapsToPartial()
    {
        var result = MakeResult(skipped: 4, errors: 1, detections: 3);
        Assert.Equal(ScanOutcome.Partial, ScanOutcomeMapper.Map(result));
    }

    // Полное покрытие с детектами — Threats.
    [Fact]
    public void FullCoverageWithDetections_MapsToThreats()
    {
        var result = MakeResult(detections: 2);
        Assert.Equal(ScanOutcome.Threats, ScanOutcomeMapper.Map(result));
    }

    // Полное покрытие без детектов — Clean. Это единственная ветка,
    // из которой допустимо писать «угроз не обнаружено».
    [Fact]
    public void FullCoverageWithoutDetections_MapsToClean()
    {
        var result = MakeResult();
        Assert.Equal(ScanOutcome.Clean, ScanOutcomeMapper.Map(result));
    }

    // Обнаружения в summary без списка DetectionDto (рассинхрон протокола)
    // не даёт Clean — считается по списку детектов, как в Runner.
    [Fact]
    public void DetectionsCountMismatch_MapsToClean_WhenListEmpty()
    {
        // Список пуст, summary.Detections > 0 — классификация по списку.
        var result = MakeResult();
        result.Summary.Detections = 5;
        Assert.Equal(ScanOutcome.Clean, ScanOutcomeMapper.Map(result));
    }
}
