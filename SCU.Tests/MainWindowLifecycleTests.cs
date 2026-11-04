using SCU.Common;
using SCU.Interop;
using Xunit;

namespace SCU.Tests;

// Аудит, п. 1: закрытие главного окна при включённом переводе раскладки.
// Два инварианта: ресурсы окна не освобождаются, если закрытие отменено
// (фоновый режим), и в трее есть штатный «Выход» из этого режима.
[Collection("LocalizationSequential")]
public sealed class MainWindowLifecycleTests
{
    [Fact]
    public void ReleaseIfClosing_CancelledClose_KeepsResourcesAlive()
    {
        var released = 0;
        var shutdown = new WindowShutdown(() => released++);

        shutdown.ReleaseIfClosing(cancelled: true);

        // Ключевая регрессия: отменённое закрытие не должно убивать ресурсы —
        // иначе окно остаётся на экране, но переводчик, подписки и горячие
        // клавиши уже мертвы, а процесс не завершается.
        Assert.Equal(0, released);
        Assert.False(shutdown.Released);

        // Пользователь вернул окно из фонового режима и закрыл его по-настоящему.
        shutdown.ReleaseIfClosing(cancelled: false);
        Assert.Equal(1, released);
        Assert.True(shutdown.Released);
    }

    [Fact]
    public void Release_IsIdempotent_AcrossClosingAndClosed()
    {
        var released = 0;
        var shutdown = new WindowShutdown(() => released++);

        // OnClosing (не отменено) и OnClosed вызывают освобождение оба —
        // Dispose ViewModel/переводчика должен выполниться ровно один раз.
        shutdown.ReleaseIfClosing(cancelled: false);
        shutdown.Release();

        Assert.Equal(1, released);
    }

    [Fact]
    public void Release_AfterCancelledClose_WorksOnClosedPath()
    {
        var released = 0;
        var shutdown = new WindowShutdown(() => released++);
        shutdown.ReleaseIfClosing(cancelled: true);

        shutdown.Release();

        Assert.Equal(1, released);
    }

    [Fact]
    public void ReleaseIfClosing_MultipleCancellations_NothingReleased()
    {
        var released = 0;
        var shutdown = new WindowShutdown(() => released++);

        // Повторные закрытия в фоновом режиме (пользователь несколько раз жмёт «X»)
        // не должны ни освобождать ресурсы, ни ломать состояние окна.
        for (var i = 0; i < 3; i++)
        {
            shutdown.ReleaseIfClosing(cancelled: true);
        }

        Assert.Equal(0, released);
    }

    [Fact]
    public void TrayMenu_HasAllSpecifiedItems_ExitAlwaysAvailable()
    {
        var entries = TrayMenu.Build(isCleanupBusy: false);

        Assert.Equal(
            [TrayMenuCommand.Clean, TrayMenuCommand.Scanner, TrayMenuCommand.Startup, TrayMenuCommand.Settings, TrayMenuCommand.Exit],
            entries.Select(entry => entry.Command).ToArray());

        var clean = entries.Single(entry => entry.Command == TrayMenuCommand.Clean);
        var exit = entries.Single(entry => entry.Command == TrayMenuCommand.Exit);

        // «Очистить» показывает проценты выполнения справа.
        Assert.True(clean.WithProgress);

        // Без «Выхода» в трее пользователь не может завершить SCU, пока закрытие
        // окна уходит в фоновый режим.
        Assert.True(exit.Enabled);
    }

    [Fact]
    public void TrayMenu_Clean_DisabledOnlyWhileCleanupBusy()
    {
        Assert.True(TrayMenu.Build(isCleanupBusy: false)
            .Single(entry => entry.Command == TrayMenuCommand.Clean).Enabled);

        // Идёт «Очистить всё»: пункт неактивен, проценты продолжают показываться.
        var entry = TrayMenu.Build(isCleanupBusy: true)
            .Single(item => item.Command == TrayMenuCommand.Clean);
        Assert.False(entry.Enabled);
        Assert.True(entry.WithProgress);
    }

    [Fact]
    public void TrayMenu_EnglishMode_AllTitlesTranslated()
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.En, notify: false);

            var titles = TrayMenu.Build(isCleanupBusy: false)
                .Select(entry => entry.Title)
                .ToArray();

            // Guard от выпадения строк трея из EN-словаря: L.T вернул бы русский ключ.
            Assert.Equal(["Clean up", "Scanner", "Startup", "Settings", "Exit"], titles);
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    [Fact]
    public void TrayMenu_RussianMode_UsesRussianTitles()
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.Ru, notify: false);

            var titles = TrayMenu.Build(isCleanupBusy: false)
                .Select(entry => entry.Title)
                .ToArray();

            Assert.Equal(["Очистить", "Сканер", "Автозагрузка", "Настройки", "Выход"], titles);
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }
}
