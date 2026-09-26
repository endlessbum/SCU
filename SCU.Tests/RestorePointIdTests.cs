using SCU.Services;
using Xunit;

namespace SCU.Tests;

// FindNewSequenceNumber: определение идентификатора новой точки восстановления
// по спискам SequenceNumber «до создания» и «после». Чистая статическая функция.
public sealed class RestorePointIdTests
{
    [Fact]
    public void NewPointAppeared_ReturnsItsSequenceNumber()
    {
        var id = RestorePointService.FindNewSequenceNumber([101, 102], [101, 102, 137]);

        Assert.Equal(137, id);
    }

    [Fact]
    public void NoNewPoint_ReturnsNull()
    {
        var id = RestorePointService.FindNewSequenceNumber([101, 102], [101, 102]);

        Assert.Null(id);
    }

    [Fact]
    public void BothListsEmpty_ReturnsNull()
    {
        Assert.Null(RestorePointService.FindNewSequenceNumber([], []));
    }

    [Fact]
    public void FirstListEmpty_AnyExistingPointIsNew()
    {
        // Первая точка на системе: списка «до» не было, любая существующая — новая.
        Assert.Equal(5, RestorePointService.FindNewSequenceNumber([], [5]));
    }

    [Fact]
    public void PointDisappeared_ReturnsNull()
    {
        // Точка удалена между запросами: «новой» её считать нельзя.
        Assert.Null(RestorePointService.FindNewSequenceNumber([101, 102], [101]));
    }

    [Fact]
    public void SeveralNewPoints_ReturnsTheLargest()
    {
        // Несколько новых (теоретически): SequenceNumber растёт монотонно,
        // наибольший принадлежит точке, созданной последней.
        var id = RestorePointService.FindNewSequenceNumber([100], [100, 205, 150]);

        Assert.Equal(205, id);
    }
}
