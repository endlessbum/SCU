using SCU.Services;
using Xunit;

namespace SCU.Tests;

// П.15в: обрезка store созданных приложением точек восстановления —
// хранится не более MaxTrackedRestorePoints (= MaxRestorePoints = 5).
public sealed class RestorePointStoreTests
{
    private static RestorePointService.AppRestorePointRecord Record(int sequence) =>
        new(sequence, DateTime.Now.AddDays(-sequence), "Точка " + sequence);

    [Fact]
    public void Trim_UnderLimit_KeepsEverything()
    {
        var records = Enumerable.Range(1, RestorePointService.MaxTrackedRestorePoints)
            .Select(Record)
            .ToList();

        var (kept, removed) = RestorePointService.TrimTrackedRestorePoints(records, RestorePointService.MaxTrackedRestorePoints);

        Assert.Empty(removed);
        Assert.Equal(RestorePointService.MaxTrackedRestorePoints, kept.Count);
    }

    [Fact]
    public void Trim_OverLimit_KeepsTenNewestAndRemovesOldest()
    {
        // 12 точек с SequenceNumber 1..12 (растёт монотонно; 12 — самая свежая).
        var records = Enumerable.Range(1, RestorePointService.MaxTrackedRestorePoints + 2)
            .Select(Record)
            .ToList();

        var (kept, removed) = RestorePointService.TrimTrackedRestorePoints(records, RestorePointService.MaxTrackedRestorePoints);

        Assert.Equal(RestorePointService.MaxTrackedRestorePoints, kept.Count);
        Assert.Equal(2, removed.Count);
        // Остались новейшие (3..12), выписаны старейшие (1, 2). Kept возвращается
        // в порядке убывания SequenceNumber (OrderByDescending внутри Trim).
        Assert.DoesNotContain(kept, r => r.SequenceNumber is 1 or 2);
        Assert.Equal(Enumerable.Range(3, RestorePointService.MaxTrackedRestorePoints).Reverse(), kept.Select(r => r.SequenceNumber));
        Assert.Equal(new[] { 2, 1 }, removed.Select(r => r.SequenceNumber));
    }

    [Fact]
    public void Trim_UnorderedInput_StillKeepsNewestBySequence()
    {
        // Вход в произвольном порядке: обрезка сортирует по SequenceNumber.
        var records = new List<RestorePointService.AppRestorePointRecord>
        {
            Record(7), Record(2), Record(11), Record(1), Record(5)
        };

        var (kept, removed) = RestorePointService.TrimTrackedRestorePoints(records, 3);

        Assert.Equal(new[] { 11, 7, 5 }, kept.Select(r => r.SequenceNumber));
        Assert.Equal(new[] { 2, 1 }, removed.Select(r => r.SequenceNumber));
    }

    [Fact]
    public void TrackedStore_SaveAndLoad_Roundtrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "restore_points.json");
            var records = new List<RestorePointService.AppRestorePointRecord>
            {
                new(137, new DateTime(2026, 9, 20, 12, 0, 0), "Перед чисткой")
            };

            RestorePointService.SaveTrackedRestorePoints(records, path);
            var loaded = RestorePointService.LoadTrackedRestorePoints(path);

            var restored = Assert.Single(loaded);
            Assert.Equal(137, restored.SequenceNumber);
            Assert.Equal("Перед чисткой", restored.Description);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }
}
