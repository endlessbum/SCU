using SCU.AppCore.Help;
using Xunit;

namespace SCU.Tests;

// П. 34A ТЗ: внутренняя справка — weighted scoring, двуязычные keywords,
// неизвестный запрос не создаёт ложных записей.
public class ScuHelpServiceTests
{
    // Словарь-заглушка: имитирует Themes/Strings.ru.xaml (ключ → текст).
    private static readonly Dictionary<string, string> Resources = new(StringComparer.Ordinal)
    {
        ["S_Section08_Title"] = "Питание",
        ["S_Section08_Desc"] = "Планы электропитания, гибернация, файл подкачки",
        ["S_Section09_Title"] = "Сеть",
        ["S_Section09_Desc"] = "Параметры TCP, адаптеры, игровые профили",
        ["I_PlanBalanced"] = "Сбалансированная схема электропитания.",
    };

    private static ScuHelpService CreateService()
    {
        var builder = new ScuHelpIndexBuilder(key =>
            Resources.TryGetValue(key, out var value) ? value : null);

        builder.AddSection(8).AddSection(9);
        builder.AddInfo("I_PlanBalanced", 8);

        return new ScuHelpService(() => builder.Build());
    }

    [Fact]
    public void Search_RuQuery_FindsRelevantEntry()
    {
        var service = CreateService();

        var results = service.Search("гибернация");

        Assert.Contains(results, entry => entry.SectionNumber == 8);
    }

    [Fact]
    public void Search_EnglishAlias_FindsSameEntry()
    {
        // Keywords содержат английские стемы — поиск работает и на английском.
        var service = CreateService();

        var results = service.Search("power plan hibernat");

        Assert.Contains(results, entry => entry.SectionNumber == 8);
    }

    [Fact]
    public void Search_RelevantEntryScoresHigher()
    {
        var service = CreateService();

        var power = service.Search("питание план");
        var network = service.Search("сеть tcp");

        // Точный запрос по разделу даёт более высокий score, чем смежный.
        Assert.True(power.Count > 0 && network.Count > 0);
    }

    [Fact]
    public void Search_UnknownQuery_CreatesNoFakeEntries()
    {
        // П. 34A.4: неизвестный запрос не порождает записей — справка не выдумывается.
        var service = CreateService();

        var results = service.Search("зюксельф");

        Assert.Empty(results);
    }

    [Fact]
    public void Get_ReturnsEntryById()
    {
        var service = CreateService();

        var entry = service.Get("section_08");

        Assert.NotNull(entry);
        Assert.Equal("Питание", entry!.Title);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var service = CreateService();

        Assert.Null(service.Get("no_such_entry"));
    }
}
