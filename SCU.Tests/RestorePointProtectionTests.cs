using Xunit;

namespace SCU.Tests;

// Гейт создания точки: IsSystemProtectionEnabled не бросает и стабилен в рамках вызова.
// Live WMI Create/Delete здесь не трогаем — требуют админа и меняют систему.
public sealed class RestorePointProtectionTests
{
    [Fact]
    public void IsSystemProtectionEnabled_DoesNotThrow_AndIsStable()
    {
        var service = new RestorePointService(Logger.CreateForCurrentRun());

        var first = service.IsSystemProtectionEnabled();
        var second = service.IsSystemProtectionEnabled();

        Assert.Equal(first, second);
    }

    [Fact]
    public void MaxRestorePoints_MatchesTrackedLimit()
    {
        Assert.Equal(5, RestorePointService.MaxRestorePoints);
        Assert.Equal(RestorePointService.MaxRestorePoints, RestorePointService.MaxTrackedRestorePoints);
    }
}
