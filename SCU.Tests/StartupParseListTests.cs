using SCU.Models;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// Парсер списка автозагрузки (tab-separated: index|source|name|command) и фильтр мусора.
public class StartupParseListTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"scu_tests_{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        File.Delete(_path);
    }

    [Fact]
    public void ParseList_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(StartupViewModel.ParseList(Path.Combine(_path, "нет_такого_файла.txt")));
    }

    [Fact]
    public void ParseList_ParsesWellFormedLines()
    {
        File.WriteAllLines(_path,
        [
            "1\tHKCU\tonenote\t\"onenote.exe\" /tsr",
            "2\tHKLM\tSecurityHealth\t\"C:\\Windows\\...\\SecurityHealthSystray.exe\"",
        ]);

        var items = StartupViewModel.ParseList(_path);

        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].Index);
        Assert.Equal("HKCU", items[0].Source);
        Assert.Equal("onenote", items[0].Name);
        Assert.Equal("\"onenote.exe\" /tsr", items[0].Command);
    }

    [Fact]
    public void ParseList_SkipsEmptyAndMalformedLines()
    {
        File.WriteAllLines(_path,
        [
            "",
            "   ",
            "не число\tHKCU\tname\tcmd",
            "3\tвсего-три-поля",
        ]);

        Assert.Empty(StartupViewModel.ParseList(_path));
    }

    [Fact]
    public void ParseList_CommandWithTabs_LosesTail()
    {
        // Задокументированное поведение: line.Split('\t') режет команду по всем табам,
        // хвост после 4-го поля теряется. См. отчёт ревью StartupViewModel.cs:302.
        File.WriteAllLines(_path, ["5\tHKCU\tapp\tpart1\tpart2"]);

        var items = StartupViewModel.ParseList(_path);

        var item = Assert.Single(items);
        Assert.Equal("part1", item.Command);
        Assert.DoesNotContain("part2", item.Command);
    }

    [Fact]
    public void IsJunk_MatchesCaseInsensitiveSystemJunk()
    {
        Assert.True(StartupViewModel.IsJunk(new StartupItem(0, "HKCU", "DESKTOP.INI", "x")));
        Assert.True(StartupViewModel.IsJunk(new StartupItem(0, "HKCU", "thumbs.db", "x")));
        Assert.False(StartupViewModel.IsJunk(new StartupItem(0, "HKCU", "OneDrive", "x")));
    }
}
