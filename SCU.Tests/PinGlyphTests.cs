using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using SCU.Common;
using SCU.Infrastructure.Logging;
using SCU.Infrastructure.Storage;
using SCU.Views.Controls;
using Xunit;

namespace SCU.Tests;

// PinGlyph вне визуального дерева: дефолты, синхронизация IsChecked/тултипа/
// AutomationProperties.Name по CardId и состоянию PinState, защита пустого
// CardId при клике. WPF-контролы требуют STA-поток — тесты исполняются в
// короткоживущем STA-потоке через RunInSta. Подписки на Loaded/Unloaded здесь
// не срабатывают (глиф не в дереве) — событийная синхронизация покрыта
// уровнем PinState.
public sealed class PinGlyphTests : IDisposable
{
    private readonly string _directory;

    public PinGlyphTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Reset();
    }

    public void Dispose()
    {
        try
        {
            Reset();
            PinState.Instance.StoreFactory = null;
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична: сбой удаления не роняет тест.
        }
    }

    private void Reset()
    {
        var store = new PinnedCardsStore(Logger.CreateForCurrentRun(),
            Path.Combine(_directory, "pinned-cards.json"));
        PinState.Instance.StoreFactory = () => store;
        PinState.Instance.ResetForTests();
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

    [Fact]
    public void Ctor_HasDefaultsAndPinTooltip() => RunInSta(() =>
    {
        var glyph = new PinGlyph();

        Assert.False(glyph.IsChecked == true);
        Assert.False(glyph.IsThreeState);
        Assert.True(glyph.Focusable);
        Assert.Equal(L.T("Закрепить карточку на главной"), glyph.ToolTip);
    });

    [Fact]
    public void CardId_WhenPinned_SyncsCheckedTooltipAndAutomationName() => RunInSta(() =>
    {
        PinState.Instance.Set("net.tcp", true);

        var glyph = new PinGlyph { CardId = "net.tcp" };

        Assert.True(glyph.IsChecked == true);
        Assert.Equal(L.T("Открепить карточку"), glyph.ToolTip);
        Assert.Equal(L.T("Открепить карточку"), AutomationProperties.GetName(glyph));
    });

    [Fact]
    public void CardId_WhenNotPinned_StaysUncheckedWithPinTooltip() => RunInSta(() =>
    {
        var glyph = new PinGlyph { CardId = "net.tcp" };

        Assert.False(glyph.IsChecked == true);
        Assert.Equal(L.T("Закрепить карточку на главной"), glyph.ToolTip);
        Assert.Equal(L.T("Закрепить карточку на главной"), AutomationProperties.GetName(glyph));
    });

    [Fact]
    public void CardId_Change_ResyncsVisualToNewCardState() => RunInSta(() =>
    {
        PinState.Instance.Set("a.first", true);

        var glyph = new PinGlyph { CardId = "a.first" };
        Assert.True(glyph.IsChecked == true);

        // «b.second» не закреплена — глиф обязан вернуться в незакреплённый вид.
        glyph.CardId = "b.second";
        Assert.False(glyph.IsChecked == true);
        Assert.Equal(L.T("Закрепить карточку на главной"), glyph.ToolTip);
    });

    [Fact]
    public void Click_EmptyCardId_RevertsVisualAndDoesNotPersist() => RunInSta(() =>
    {
        var glyph = new PinGlyph();

        // Имитация клика по глифу без CardId: тумблер переключился, обработчик
        // обязан вернуть визуал из PinState и ничего не закрепить.
        glyph.IsChecked = true;
        glyph.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.False(glyph.IsChecked == true);
        Assert.Empty(PinState.Instance.PinnedIds);
    });

    [Fact]
    public void Click_TogglesPinStateByCardId() => RunInSta(() =>
    {
        var glyph = new PinGlyph { CardId = "t.click" };

        glyph.IsChecked = true;
        glyph.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(PinState.Instance["t.click"]);

        glyph.IsChecked = false;
        glyph.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.False(PinState.Instance["t.click"]);
    });

    [Fact]
    public void PinCard_AttachedProperty_RoundtripWithEmptyDefault() => RunInSta(() =>
    {
        var host = new System.Windows.Controls.ContentControl();

        Assert.Equal(string.Empty, PinCard.GetCardId(host));

        PinCard.SetCardId(host, "net.dns");
        Assert.Equal("net.dns", PinCard.GetCardId(host));
    });
}
