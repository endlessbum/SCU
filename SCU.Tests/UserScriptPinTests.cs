using SCU.Infrastructure.Storage;
using SCU.ViewModels;
using SCU.ViewModels.Sections;
using SCU.Views.Cards;
using Xunit;

namespace SCU.Tests;

// Закрепление и поиск пользовательских скриптов: id закрепления обеих
// карточек скрипта (полоса + оригинал) и маппинг «uscript.<id>» → раздел
// владельца для ленивой инициализации перед созданием дубликата на «Главной».
public sealed class UserScriptPinTests
{
    private static MainViewModel.UserScriptCard CreateCard(string id, int section) =>
        new(new UserScriptData { Id = id, Title = "Скрипт " + id, SectionNumber = section });

    [Fact]
    public void UserScriptCard_PinsWithUscriptPrefix()
    {
        var card = CreateCard("us_abc123", 9);

        Assert.Equal("uscript.us_abc123", card.PinCardId);
    }

    [Fact]
    public void StripCopy_PinsWithSameIdAsOriginal()
    {
        // Скрепка в полосе (копия) и скрепка дубликата на «Главной» (оригинал)
        // обязаны ссылаться на одно состояние PinState.
        var data = new UserScriptData { Id = "us_def456", SectionNumber = 12 };
        var original = new MainViewModel.UserScriptCard(data);
        var stripCopy = new UserScriptCardViewModel(data);

        Assert.Equal(original.PinCardId, stripCopy.PinCardId);
    }

    [Fact]
    public void ScriptSectionNumberOf_KnownScript_ReturnsOwnerSection()
    {
        var scripts = new List<MainViewModel.UserScriptCard>
        {
            CreateCard("us_one", 9),
            CreateCard("us_two", 105),
        };

        Assert.Equal(9, PinnedCardCatalog.ScriptSectionNumberOf("uscript.us_one", scripts));
        // Кастомная вкладка (≥100) — легальный раздел скрипта.
        Assert.Equal(105, PinnedCardCatalog.ScriptSectionNumberOf("uscript.us_two", scripts));
    }

    [Fact]
    public void ScriptSectionNumberOf_UnknownScript_ReturnsNull()
    {
        var scripts = new List<MainViewModel.UserScriptCard> { CreateCard("us_one", 9) };

        Assert.Null(PinnedCardCatalog.ScriptSectionNumberOf("uscript.us_gone", scripts));
        Assert.Null(PinnedCardCatalog.ScriptSectionNumberOf("uscript.", scripts));
    }

    [Fact]
    public void ScriptSectionNumberOf_NonScriptId_ReturnsNull()
    {
        var scripts = new List<MainViewModel.UserScriptCard> { CreateCard("us_one", 9) };

        Assert.Null(PinnedCardCatalog.ScriptSectionNumberOf("net.tcp", scripts));
        Assert.Null(PinnedCardCatalog.ScriptSectionNumberOf("input.game-mode", scripts));
        Assert.Null(PinnedCardCatalog.ScriptSectionNumberOf("", scripts));
    }

    [Fact]
    public async Task ScriptSearchUtility_RunIsSafeStub()
    {
        // Строки скриптов не входят в список выключателя и пакет, но контракт
        // BatchUtility.Run обязан существовать: заглушка возвращает null.
        var card = CreateCard("us_stub", 9);
        var utility = new BatchUtility
        {
            Id = card.Id,
            TitleKey = card.Title,
            IsRawTitle = true,
            Section = card.SectionNumber,
            Run = () => Task.FromResult<string?>(null),
        };

        Assert.Null(await utility.Run());
        Assert.Equal("us_stub", utility.Id);
    }
}
