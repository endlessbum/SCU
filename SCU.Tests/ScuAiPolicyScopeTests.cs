using SCU.AppCore.AI;
using SCU.AppCore.Help;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// П. 34B ТЗ: локальная классификация намерения. Guard не единственная граница
// безопасности (registry валидирует всегда), но out-of-scope отсекается здесь,
// без обращения к API.
public class ScuAiPolicyScopeTests
{
    // Справка-заглушка: для вопросов о SCU имитируется найденная запись.
    private static ScuAiIntent Classify(string query, bool helpFound) =>
        ScuAiPolicy.ClassifyIntent(query, _ => helpFound
            ? (IReadOnlyList<ScuHelpEntry>)new[]
            {
                new ScuHelpEntry { Id = "section_08", SectionNumber = 8, Title = "Питание" },
            }
            : Array.Empty<ScuHelpEntry>());

    [Theory]
    [InlineData("какая сейчас погода в москве?")]
    [InlineData("расскажи новости за сегодня")]
    [InlineData("напиши стих про осень")]
    [InlineData("who will win the world cup?")]
    public void OutOfScope_Queries_ClassifiedAsUnsupported(string query)
    {
        Assert.Equal(ScuAiIntent.Unsupported, Classify(query, helpFound: false));
    }

    [Fact]
    public void ScuQuestion_ClassifiedAsHelp()
    {
        // П. 34B.7: вопрос о функции SCU — Help.
        Assert.Equal(ScuAiIntent.Help, Classify("как работает гибернация?", helpFound: true));
    }

    [Fact]
    public void NavigationRequest_ClassifiedAsNavigate()
    {
        // П. 34B.8: «открой раздел» — Navigate.
        Assert.Equal(ScuAiIntent.Navigate, Classify("открой раздел сеть", helpFound: true));
    }

    [Fact]
    public void ChangeRequest_ClassifiedAsChangeSetting()
    {
        // П. 34B.9: «включи/отключи» + найденная справка — ChangeSetting.
        Assert.Equal(ScuAiIntent.ChangeSetting, Classify("отключи гибернацию", helpFound: true));
    }

    [Fact]
    public void ChangeRequest_WithoutHelp_IsNotChange()
    {
        // Нет записи справки — это не подтверждённая функция SCU: guard не
        // относит запрос к изменению настроек (п. 38: не выдумывать настройки).
        Assert.NotEqual(ScuAiIntent.ChangeSetting, Classify("включи квазимодо", helpFound: false));
    }

    [Fact]
    public void EmptyQuery_ClassifiedAsUnknown()
    {
        Assert.Equal(ScuAiIntent.Unknown, Classify(string.Empty, helpFound: false));
        Assert.Equal(ScuAiIntent.Unknown, Classify("   ", helpFound: false));
    }
}
