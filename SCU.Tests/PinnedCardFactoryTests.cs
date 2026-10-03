using SCU.Infrastructure.Logging;
using SCU.Views.Cards;
using Xunit;

namespace SCU.Tests;

// Фабрика закреплённых карточек «Главной» (бывшая лямбда
// MainWindow.CreateDashboardView): неизвестный id из pinned-cards.json
// обязан попадать в журнал с префиксом PINCARDS (иначе молча пропавшие
// закрепления не отследить), известный — не попадать. MainViewModel в тестах
// не строится (тянет весь граф сервисов): передаётся null, ветка известного
// id в этом случае ограничивается резолвингом раздела без его инициализации.
public sealed class PinnedCardFactoryTests
{
    private static (Logger Logger, List<string> Lines) CreateLoggingLogger()
    {
        var lines = new List<string>();
        var logger = Logger.CreateForCurrentRun();
        logger.LineWritten += lines.Add;
        return (logger, lines);
    }

    private static readonly string[] KnownIds =
    [
        "net.tcp",
        "maint.winsxs",
        "update.pause",
        "drivers.state",
        "ai.assistant",
        "settings.addscript",
        "input.mouse-acceleration",
    ];

    [Fact]
    public void Create_UnknownId_WarnsAndReturnsNull()
    {
        var (logger, lines) = CreateLoggingLogger();

        Assert.Null(PinnedCardFactory.Create("totally-unknown", null, logger));
        Assert.Null(PinnedCardFactory.Create("maint.gone", null, logger));

        Assert.Equal(1, lines.Count(line =>
            line.EndsWith(PinnedCardFactory.UnknownIdLogPrefix + "totally-unknown", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(line =>
            line.EndsWith(PinnedCardFactory.UnknownIdLogPrefix + "maint.gone", StringComparison.Ordinal)));
    }

    [Fact]
    public void Create_UnknownScriptId_WarnsAndReturnsNull()
    {
        // «uscript.<id>» с удалённым скриптом — тоже неизвестный id: раздел
        // не резолвится, карточка не создаётся, warn пишется.
        var (logger, lines) = CreateLoggingLogger();

        Assert.Null(PinnedCardFactory.Create("uscript.us_gone", null, logger));

        Assert.Contains(lines, line =>
            line.EndsWith(PinnedCardFactory.UnknownIdLogPrefix + "uscript.us_gone", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_KnownId_DoesNotWarn()
    {
        var (logger, lines) = CreateLoggingLogger();

        foreach (var cardId in PinnedCardCatalog.KnownCardIds.Append("input.game-bar"))
        {
            // main = null: ветка известного id обязана резолвить раздел до
            // любого обращения к MainViewModel — иначе закреплённая карточка
            // упала бы на ровном месте.
            PinnedCardFactory.Create(cardId, null, logger);
        }

        Assert.DoesNotContain(lines, line => line.Contains(PinnedCardFactory.UnknownIdLogPrefix, StringComparison.Ordinal));
    }
}
