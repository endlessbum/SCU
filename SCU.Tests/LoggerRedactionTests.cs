using SCU.Infrastructure.Logging;
using Xunit;

namespace SCU.Tests;

// П. 34F.31 ТЗ: журналы не должны раскрывать авторизационные данные.
// Ключ передаётся только в заголовке HTTP-запроса, но редукция работает
// «в глубину»: любая случайная строка с секретом маскируется до записи.
public class LoggerRedactionTests
{
    [Fact]
    public void Info_Line_WithBearerToken_IsMasked()
    {
        var logger = Logger.CreateForCurrentRun();
        const string secret = "sk-abcdef1234567890abcdef1234567890";

        logger.Info($"SCU_AI | request | Authorization: Bearer {secret} | model=deepseek-chat");

        var snapshot = Assert.Single(
            logger.Snapshot(), line => line.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(secret, snapshot);
        Assert.Contains("[REDACTED]", snapshot);
    }

    [Fact]
    public void Raw_Line_WithApiKeyAssignment_IsMasked()
    {
        var logger = Logger.CreateForCurrentRun();
        var secret = "sk-or-v1-" + new string('a', 48);

        logger.Raw($"DEEPSEEK | api_key = {secret} | cached");

        var snapshot = Assert.Single(
            logger.Snapshot(), line => line.Contains("api_key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(secret, snapshot);
        Assert.Contains("[REDACTED]", snapshot);
    }

    [Fact]
    public void Ordinary_Message_IsNotTouched()
    {
        // Маскирование не ломает обычные диагностические строки:(bin) самый
        // частый формат логов SCU — метка + параметры.
        var logger = Logger.CreateForCurrentRun();

        logger.Info("SCU_AI | registry | tools=19");
        logger.Info("SERVICES | refresh | count=42 | admin=True");

        Assert.Equal(2, logger.Snapshot().Count);
    }
}
