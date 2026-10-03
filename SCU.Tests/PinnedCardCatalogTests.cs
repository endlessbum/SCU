using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SCU.Common;
using SCU.Infrastructure.Logging;
using SCU.ViewModels;
using SCU.ViewModels.Sections;
using SCU.Views.Cards;
using SCU.Views.Controls;
using Xunit;

namespace SCU.Tests;

// Реестр закрепляемых карточек: маппинг id → раздел-владелец (нужен для
// ленивой инициализации перед созданием дубликата на «Главной») и поведение
// на неизвестных id. Создание карточек с реальным MainViewModel здесь не
// проверяется (тянет весь граф сервисов) — Create тестируется только на
// ветке «неизвестный id», которая не разыменовывает владельца; обёртка
// тумблерной карточки проверяется через internal-сеам CreateInputCard.
public sealed class PinnedCardCatalogTests
{
    private sealed class NoDialogs : IConfirmDialogService
    {
        public bool Ask(string title, string message, string? confirmText = null) => false;
    }

    private static void RunInSta(Action action) => RunInSta<object?>(() =>
    {
        action();
        return null;
    });

    private static T RunInSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }

        return result!;
    }

    // Ожидаемый состав каталога: синхронизирован с единственной таблицей
    // PinnedCardCatalog.Cards. Удаление/переименование строки таблицы роняет
    // этот тест, а значит и закрепление карточки пользователями.
    private static readonly string[] ExpectedCatalogIds =
    [
        "net.gaming",
        "net.tcp",
        "net.mtu",
        "net.qos",
        "net.netbios",
        "net.dns",
        "maint.integrity",
        "maint.winsxs",
        "update.services",
        "update.pause",
        "drivers.state",
        "ai.assistant",
        "settings.addscript",
    ];

    [Fact]
    public void KnownCardIds_MatchExpectedCatalog()
    {
        Assert.Equal(
            ExpectedCatalogIds.ToHashSet(StringComparer.Ordinal),
            PinnedCardCatalog.KnownCardIds.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void SectionNumberOf_AllKnownCardIds_MapToOwnerSections()
    {
        var expected = new (string Id, int Section)[]
        {
            ("net.gaming", 9),
            ("net.tcp", 9),
            ("net.mtu", 9),
            ("net.qos", 9),
            ("net.netbios", 9),
            ("net.dns", 9),
            ("maint.integrity", 12),
            ("maint.winsxs", 12),
            ("update.services", 16),
            ("update.pause", 16),
            ("drivers.state", 25),
            ("ai.assistant", 24),
            ("settings.addscript", 17),
        };

        Assert.All(expected, item =>
            Assert.Equal(item.Section, PinnedCardCatalog.SectionNumberOf(item.Id)));
    }

    [Fact]
    public void SectionNumberOf_EveryKnownCardId_HasOwnerSection()
    {
        // Раньше список id дублировался в SectionNumberOf и Create — карточка
        // могла закрепляться, но её дубликат молча не создавался. Теперь оба
        // метода читают одну таблицу, и инвариант «каждому известному id
        // соответствует и раздел, и фабрика» проверяется напрямую.
        Assert.All(PinnedCardCatalog.KnownCardIds, id =>
            Assert.True(PinnedCardCatalog.SectionNumberOf(id) is not null,
                "Карточке '" + id + "' не назначен раздел-владелец."));
    }

    [Fact]
    public void SectionNumberOf_InputPrefix_MapsToSection11()
    {
        // Любой id с префиксом input.* — раздел «Ввод», даже будущие тумблеры;
        // существование строки проверит Create (нет строки → null).
        Assert.Equal(11, PinnedCardCatalog.SectionNumberOf("input.mouse-acceleration"));
        Assert.Equal(11, PinnedCardCatalog.SectionNumberOf("input.game-dvr"));
        Assert.Equal(11, PinnedCardCatalog.SectionNumberOf("input.any-future-switch"));
    }

    [Fact]
    public void SectionNumberOf_UnknownId_ReturnsNull()
    {
        Assert.Null(PinnedCardCatalog.SectionNumberOf("totally-unknown"));
        Assert.Null(PinnedCardCatalog.SectionNumberOf("maint.gone"));
        Assert.Null(PinnedCardCatalog.SectionNumberOf(""));
    }

    [Fact]
    public void SectionNumberOf_IsCaseInsensitive()
    {
        // PinState сравнивает id без учёта регистра: id из pinned-cards.json
        // в любом регистре обязан находить карточку-владельца.
        Assert.Equal(9, PinnedCardCatalog.SectionNumberOf("NET.TCP"));
        Assert.Equal(12, PinnedCardCatalog.SectionNumberOf("Maint.WinSxs"));
        Assert.Equal(11, PinnedCardCatalog.SectionNumberOf("Input.Game-Bar"));
    }

    [Fact]
    public void Create_UnknownId_ReturnsNull()
    {
        // Ветка неизвестного id не разыменовывает владельца — null допустим.
        Assert.Null(PinnedCardCatalog.Create("totally-unknown", null!));
        Assert.Null(PinnedCardCatalog.Create("maint.gone", null!));
    }

    // ===== Обёртка тумблерной карточки (CreateInputCard) =====
    // Контракт PinnedSwitchCardHost: DataContext обёртки — InputViewModel
    // (стиль SwitchRowCard берёт команды раздела у ближайшего UserControl),
    // внутренний контент привязан к SwitchRow, скрепка помечена CardId.

    [Fact]
    public void CreateInputCard_KnownRow_WrapsSwitchRowUnderInputContext() => RunInSta(() =>
    {
        var input = new InputViewModel(Logger.CreateForCurrentRun(), new NoDialogs());
        var row = input.Rows.Single(r => r.Id == "mouse-acceleration");

        var host = (UserControl)PinnedCardCatalog.CreateInputCard("input.mouse-acceleration", input)!;

        Assert.Same(input, host.DataContext);
        var content = Assert.IsType<ContentControl>(host.Content);
        Assert.Same(row, content.DataContext);
        Assert.Equal(row.PinCardId, PinCard.GetCardId(content));
        Assert.Equal("input.mouse-acceleration", row.PinCardId);
    });

    [Fact]
    public void CreateInputCard_UnknownRow_ReturnsNull() => RunInSta(() =>
    {
        var input = new InputViewModel(Logger.CreateForCurrentRun(), new NoDialogs());

        Assert.Null(PinnedCardCatalog.CreateInputCard("input.no-such-row", input));
        // Пустой суффикс префикса — тоже «строки нет».
        Assert.Null(PinnedCardCatalog.CreateInputCard("input.", input));
    });
}
