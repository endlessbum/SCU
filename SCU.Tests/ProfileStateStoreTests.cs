using SCU.Common;
using SCU.Models;
using Xunit;

namespace SCU.Tests;

// ProfileStateStore (profile.json): roundtrip состояния, вытеснение старой записи
// (ёмкость 1) и восстановление после повреждения файла — по образцу тестов HistoryStore.
public sealed class ProfileStateStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;
    private readonly Logger _logger;

    public ProfileStateStoreTests()
    {
        // Уникальная временная папка на каждый тест; удаление — в Dispose.
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "profile.json");
        _logger = Logger.CreateForCurrentRun();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична: сбой удаления не роняет тест.
        }
    }

    private ProfileStateStore CreateStore() => new(_logger, _filePath);

    [Fact]
    public async Task SaveAsync_LoadAsync_RoundtripPreservesState()
    {
        var state = new CustomProfileState
        {
            ActiveProfile = ProfileService.ProfileCustom,
            CustomSteps = new Dictionary<string, StepDesire>
            {
                ["power.ultimate"] = StepDesire.On,
                ["games.gamebar"] = StepDesire.Off
            }
        };

        await CreateStore().SaveAsync(state);
        var loaded = await CreateStore().LoadAsync();

        Assert.Equal(ProfileService.ProfileCustom, loaded.ActiveProfile);
        Assert.Equal(2, loaded.CustomSteps.Count);
        Assert.Equal(StepDesire.On, loaded.CustomSteps["power.ultimate"]);
        Assert.Equal(StepDesire.Off, loaded.CustomSteps["games.gamebar"]);
    }

    [Fact]
    public async Task LoadAsync_EmptyStore_ReturnsDefaultState()
    {
        var loaded = await CreateStore().LoadAsync();

        // Отсутствующий файл = «не следить ни за каким профилем», словарь пуст.
        Assert.Null(loaded.ActiveProfile);
        Assert.Empty(loaded.CustomSteps);
    }

    [Fact]
    public async Task SaveAsync_Twice_KeepsOnlyLatestState()
    {
        var store = CreateStore();
        await store.SaveAsync(new CustomProfileState
        {
            ActiveProfile = ProfileService.ProfileGaming,
            CustomSteps = new Dictionary<string, StepDesire> { ["games.gamebar"] = StepDesire.Off }
        });
        await store.SaveAsync(new CustomProfileState
        {
            ActiveProfile = ProfileService.ProfileBalanced,
            CustomSteps = []
        });

        var loaded = await CreateStore().LoadAsync();

        // Ёмкость 1: старое состояние вытесняется, читается только последнее.
        Assert.Equal(ProfileService.ProfileBalanced, loaded.ActiveProfile);
        Assert.Empty(loaded.CustomSteps);
    }

    [Fact]
    public async Task LoadAsync_CorruptedFile_ReturnsDefaultAndMovesFileAside()
    {
        await File.WriteAllTextAsync(_filePath, "не json вовсе");

        var loaded = await CreateStore().LoadAsync();

        Assert.Null(loaded.ActiveProfile);
        Assert.False(File.Exists(_filePath));
        // Повреждённый файл сохранён рядом под именем *.corrupt-<timestamp>.
        Assert.NotEmpty(Directory.GetFiles(_directory, "profile.json.corrupt-*"));
    }

    [Fact]
    public async Task LoadAsync_UnknownProfileId_PreservedAsIs()
    {
        // Активный профиль читается как строка без валидации: решение «известен ли он»
        // принимает VM (GetTarget возвращает null для неизвестных id).
        await CreateStore().SaveAsync(new CustomProfileState
        {
            ActiveProfile = "future-profile",
            CustomSteps = new Dictionary<string, StepDesire> { ["network.gaming"] = StepDesire.On }
        });

        var loaded = await CreateStore().LoadAsync();

        Assert.Equal("future-profile", loaded.ActiveProfile);
        Assert.Equal(StepDesire.On, loaded.CustomSteps["network.gaming"]);
    }
}
