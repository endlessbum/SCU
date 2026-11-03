using SCU.Common;
using Xunit;

namespace SCU.Tests;

// IsPaused/GetPauseInfoText читают живой реестр (HKLM), поэтому тесты — инварианты
// согласованности, не зависящие от состояния машины: реестр не мутируем.
// PauseUpdates(days) вне [1,35] — чистая валидация без записи в реестр.
public class UpdateServicePauseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(36)]
    [InlineData(100)]
    public void PauseUpdates_OutOfRangeDays_FailsWithoutTouchingRegistry(int days)
    {
        var service = new UpdateService(Logger.CreateForCurrentRun(), new RegistryHelper(Logger.CreateForCurrentRun()));
        var result = service.PauseUpdates(days);

        Assert.False(result.IsSuccess);
        Assert.Contains("1 до 35", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPaused_AgreesWithPauseInfoText()
    {
        var text = UpdateService.GetPauseInfoText();
        var paused = UpdateService.IsPaused();

        if (text.StartsWith("Обновления приостановлены", StringComparison.Ordinal))
        {
            Assert.True(paused);
        }
        else
        {
            // «Пауза не задана», «Пауза истекла», нераспознанный срок, ошибка чтения — не активная пауза.
            Assert.False(paused);
        }
    }

    [Fact]
    public void IsPaused_IsStableAcrossCalls()
    {
        // Чистое чтение: два вызова подряд в пределах секунды дают один результат.
        Assert.Equal(UpdateService.IsPaused(), UpdateService.IsPaused());
    }

    [Fact]
    public void GetPauseInfoText_ReturnsKnownMessage()
    {
        var text = UpdateService.GetPauseInfoText();

        Assert.True(
            text.StartsWith("Пауза не задана", StringComparison.Ordinal)
            || text.StartsWith("Обновления приостановлены", StringComparison.Ordinal)
            || text.StartsWith("Пауза истекла", StringComparison.Ordinal)
            || text.StartsWith("Пауза задана", StringComparison.Ordinal)
            || text.StartsWith("Не удалось прочитать", StringComparison.Ordinal),
            "Неизвестный текст паузы: " + text);
    }
}
