using SCU.Common;
using Xunit;

namespace SCU.Tests;

// MenuCustomizationStore: сохранение/загрузка модели кастомизации меню.
public sealed class MenuCustomizationStoreTests : IDisposable
{
    private readonly string _directory;

    public MenuCustomizationStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична.
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new MenuCustomizationStore(NullLogger, Path.Combine(_directory, "menu.json"));
        var menu = store.Load();

        Assert.Empty(menu.Hidden);
        Assert.Empty(menu.Titles);
        Assert.Empty(menu.Groups);
        Assert.Empty(menu.DeletedUtils);
        Assert.Empty(menu.CustomSections);
    }

    [Fact]
    public void SaveLoad_Roundtrip_PreservesModel()
    {
        var path = Path.Combine(_directory, "menu.json");
        var store = new MenuCustomizationStore(NullLogger, path);
        var original = new MenuCustomization
        {
            Hidden = [4, 21],
            Titles = new Dictionary<string, string> { ["4"] = "Мусор" },
            Groups = new Dictionary<string, string> { ["5"] = "Моя группа" },
            DeletedUtils = ["row_game-bar"],
            CustomSections =
            [
                new CustomSectionData
                {
                    Id = 100,
                    Title = "Мои тумблеры",
                    Group = "Моя группа",
                    Utils = ["row_game-bar", "update_drivers_exclude"],
                },
            ],
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original.Hidden, loaded.Hidden);
        Assert.Equal("Мусор", loaded.Titles["4"]);
        Assert.Equal("Моя группа", loaded.Groups["5"]);
        Assert.Equal(original.DeletedUtils, loaded.DeletedUtils);
        var custom = Assert.Single(loaded.CustomSections);
        Assert.Equal(100, custom.Id);
        Assert.Equal("Мои тумблеры", custom.Title);
        Assert.Equal(2, custom.Utils.Count);
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsEmpty()
    {
        var path = Path.Combine(_directory, "menu.json");
        File.WriteAllText(path, "{ broken json");

        var menu = new MenuCustomizationStore(NullLogger, path).Load();

        Assert.Empty(menu.CustomSections);
    }

    private static Logger NullLogger => Logger.CreateForCurrentRun();
}
