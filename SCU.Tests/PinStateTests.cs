using SCU.Common;
using SCU.Infrastructure.Logging;
using SCU.Infrastructure.Storage;
using Xunit;

namespace SCU.Tests;

// PinState — singleton состояния закреплений. Тесты подменяют хранилище
// временным файлом (StoreFactory) и сбрасывают состояние до/после каждого
// теста: singleton не должен зависеть от порядка выполнения тестов, а
// %AppData% не должен затрагиваться.
public sealed class PinStateTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;

    public PinStateTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "pinned-cards.json");
        Reset();
    }

    public void Dispose()
    {
        try
        {
            Reset();
            // Singleton остаётся без подменённого хранилища.
            PinState.Instance.StoreFactory = null;
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична: сбой удаления не роняет тест.
        }
    }

    private PinState State => PinState.Instance;

    private PinnedCardsStore CreateStore() => new(Logger.CreateForCurrentRun(), _filePath);

    private void Reset()
    {
        var store = CreateStore();
        PinState.Instance.StoreFactory = () => store;
        PinState.Instance.ResetForTests();
    }

    [Fact]
    public void Set_PinAndUnpin_ReflectsInIndexer()
    {
        State.Set("net.tcp", true);
        Assert.True(State["net.tcp"]);
        // Сравнение регистронезависимое, как в файле, так и в XAML-биндингах.
        Assert.True(State["NET.TCP"]);

        State.Set("net.tcp", false);
        Assert.False(State["net.tcp"]);
    }

    [Fact]
    public void Set_PreservesPinOrderAndReappendGoesLast()
    {
        State.Set("a.first", true);
        State.Set("b.second", true);
        State.Set("c.third", true);
        State.Set("b.second", false);
        State.Set("b.second", true);

        // Область на «Главной» показывает карточки в порядке закрепления:
        // повторное закрепление ставит карточку в конец.
        Assert.Equal(["a.first", "c.third", "b.second"], State.PinnedIds);
    }

    [Fact]
    public void Set_SameValue_SkipsSaveAndChangedButResyncsGlyphs()
    {
        State.Set("net.tcp", true);

        var changed = 0;
        var cardChanged = 0;
        State.Changed += () => changed++;
        State.CardPinChanged += _ => cardChanged++;

        State.Set("net.tcp", true);
        State.Set("net.tcp", false);
        State.Set("net.tcp", false);

        // Запись/пересборка области — только на реальном изменении,
        // а CardPinChanged при no-op тоже стреляет: принудительная
        // пересинхронизация глифов, разошедшихся визуалом.
        // Здесь: два no-op + один реальный unpin = 3 вызова.
        Assert.Equal(1, changed);
        Assert.Equal(3, cardChanged);
    }

    [Fact]
    public void Set_FiresCardPinChangedWithIdAndChanged()
    {
        var changed = 0;
        var pinnedIds = new List<string>();
        State.Changed += () => changed++;
        State.CardPinChanged += id => pinnedIds.Add(id);

        State.Set("maint.winsxs", true);
        State.Set("maint.winsxs", false);

        Assert.Equal(2, changed);
        Assert.Equal(["maint.winsxs", "maint.winsxs"], pinnedIds);
    }

    [Fact]
    public void Set_PersistsToStore_AndRestoresAfterReset()
    {
        State.Set("net.qos", true);
        State.Set("input.game-mode", true);

        // Файл записан (temp + move).
        var saved = CreateStore().Load();
        Assert.Equal(["net.qos", "input.game-mode"], saved);

        // После сброса singleton восстанавливает набор с диска.
        State.ResetForTests();
        State.EnsureLoaded();
        Assert.Equal(["net.qos", "input.game-mode"], State.PinnedIds);
    }

    [Fact]
    public void Replace_DeduplicatesIds()
    {
        State.Replace(["net.tcp", "net.tcp", "net.dns"]);

        Assert.Equal(["net.tcp", "net.dns"], State.PinnedIds);
    }

    [Fact]
    public void PinnedIds_ReturnsIndependentSnapshot()
    {
        State.Set("a.first", true);

        var snapshot = State.PinnedIds;
        State.Set("b.second", true);

        // Снимок не меняется вместе с состоянием.
        Assert.Single(snapshot);
        Assert.Equal(["a.first", "b.second"], State.PinnedIds);
    }

    [Fact]
    public void Set_MemoryOnly_WhenStoreUnavailable()
    {
        // Хранилище недоступно (сбой диска): закрепление продолжает работать
        // в памяти, ничего не падает и в файл не пишется.
        PinState.Instance.StoreFactory = () => throw new IOException("disk full");
        PinState.Instance.ResetForTests();

        State.Set("net.tcp", true);

        Assert.True(State["net.tcp"]);
        Assert.Empty(CreateStore().Load());
    }

    [Fact]
    public void EnsureLoaded_MergesFileOnce_IgnoringDuplicates()
    {
        File.WriteAllText(_filePath, """["a.first", "a.first", "b.second"]""");

        State.ResetForTests();
        State.EnsureLoaded();

        Assert.Equal(["a.first", "b.second"], State.PinnedIds);
    }

    [Fact]
    public void EnsureLoaded_MatchesIdsIgnoringCase()
    {
        // Регистр в файле не обязан совпадать с регистром запросов индексатора.
        File.WriteAllText(_filePath, """["Net.TCP"]""");

        State.ResetForTests();
        State.EnsureLoaded();

        Assert.True(State["net.tcp"]);
        Assert.Single(State.PinnedIds);
    }

    [Fact]
    public void Replace_ClearsStateAndFiresChanged()
    {
        State.Set("x.1", true);

        var changed = 0;
        State.Changed += () => changed++;
        State.Replace([]);

        Assert.Empty(State.PinnedIds);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Set_ConcurrentPins_AllPresentInState()
    {
        // Смоук потокобезопасности: мутации состояния под lock. Хранилище
        // прогреваем заранее (Store-геттер не потокобезопасен по замыслу —
        // в приложении он вызывается только с UI-потока).
        State.EnsureLoaded();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                State.Set($"c.{t}.{i}", true);
            }
        })));

        Assert.Equal(100, State.PinnedIds.Count);
    }
}
