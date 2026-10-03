using SCU.Common;
using SCU.Infrastructure.Logging;
using SCU.Infrastructure.Windows;
using SCU.Infrastructure.Windows.Tasks;
using SCU.Interop;
using SCU.ViewModels;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// Интеграционная проверка поиска «Главной» на настоящем DashboardViewModel:
// конструкторы всех VM-зависимостей не делают IO (чтение разделов — лениво),
// поэтому граф собирается в тесте и поиск прогоняется как в приложении.
// Регрессия «поиск перестал работать» должна падать здесь, а не только у
// пользователя.
public sealed class DashboardSearchIntegrationTests
{
    private sealed class NoDialogs : IConfirmDialogService
    {
        public bool Ask(string title, string message, string? confirmText = null) => false;
    }

    private static DashboardViewModel CreateDashboard()
    {
        var logger = Logger.CreateForCurrentRun();
        var runner = new SCURunner(logger);
        var history = new HistoryStore(logger);
        var dialogs = new NoDialogs();
        var longRunner = new LongProcessRunner(logger);

        var dashboard = new DashboardViewModel(
            logger,
            history,
            new BatchStateStore(logger),
            new CleanupViewModel(logger, runner, new FileCleanupService(logger), new ServiceManager(), dialogs, history),
            new PrivacyViewModel(logger, runner, dialogs),
            new ServicesViewModel(logger, runner, new ServiceManager(), history, dialogs),
            new TasksViewModel(logger, runner, new TaskManager(runner), dialogs, history),
            new PowerViewModel(logger, longRunner, dialogs, history),
            new NetworkViewModel(logger, dialogs, history),
            new UIViewModel(logger, dialogs),
            new InputViewModel(logger, dialogs),
            new MaintenanceViewModel(logger, longRunner, dialogs, history),
            new SecurityViewModel(logger, dialogs),
            new UpdateViewModel(logger, runner, dialogs, history));
        return dashboard;
    }

    [Fact]
    public void EmptySearch_ShowsAllGroupsAndRows()
    {
        var dashboard = CreateDashboard();

        dashboard.SearchText = string.Empty;

        Assert.NotEmpty(dashboard.Groups);
        Assert.All(dashboard.Groups, group => Assert.True(group.IsVisible));
        Assert.All(dashboard.Rows, row => Assert.True(row.IsVisible));
    }

    [Theory]
    [InlineData("очист", true)]     // keywords утилиты очистки — список на «Главной» фильтруется
    [InlineData("корзин", true)]    // заголовок утилиты очистки
    public void KnownQueries_ProduceSuggestions(string query, bool expectVisibleGroups)
    {
        var dashboard = CreateDashboard();

        dashboard.SearchText = query;

        Assert.True(dashboard.Suggestions.Count > 0,
            "Поиск по '" + query + "' не дал подсказок.");
        Assert.Equal(expectVisibleGroups, dashboard.HasVisibleGroups);
    }

    [Theory]
    [InlineData("бэкап")]   // шапочные кнопки «Бэкап» разделов «Службы»/«Задачи»
    [InlineData("служб")]   // их keywords — больше не индексируются
    public void HeaderButtons_AreNotSearchable(string query)
    {
        // Шапочные кнопки разделов («Бэкап», «Откатить», «Сбросить») — не
        // карточки: в подсказках их быть не должно. Совпадения с их заголовком
        // ищем по реестру: в тестовой среде словари не загружены, Title у
        // утилит — ключ ресурса (TitleKey).
        var dashboard = CreateDashboard();
        var buttonTitles = dashboard.Utilities
            .Where(u => !u.ShowInSearch)
            .Select(u => u.TitleKey)
            .ToHashSet(StringComparer.Ordinal);

        dashboard.SearchText = query;

        Assert.DoesNotContain(dashboard.Suggestions, s => buttonTitles.Contains(s.Title));
    }

    [Fact]
    public void SetScriptSearchRows_DoesNotBreakUtilitySearch()
    {
        var dashboard = CreateDashboard();
        dashboard.SetScriptSearchRows([]);

        dashboard.SearchText = "корзин";

        Assert.True(dashboard.Suggestions.Count > 0);
    }

    [Fact]
    public void Search_ClearsBackToFullList()
    {
        var dashboard = CreateDashboard();

        dashboard.SearchText = "корзин";
        Assert.True(dashboard.Suggestions.Count > 0);

        dashboard.SearchText = string.Empty;
        Assert.Empty(dashboard.Suggestions);
        Assert.All(dashboard.Groups, group => Assert.True(group.IsVisible));
    }

    [Fact]
    public void SearchSynonyms_РезервFamily_Expanded()
    {
        // Автозамена «резерв»→«бэкап» в e54996d затёрла ключ синонима и создала
        // дубликат ["бэкап"]: поиск по «резерв» должен снова разворачиваться.
        var synonyms = typeof(DashboardSearchService)
            .GetField("SearchSynonyms", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as System.Collections.IDictionary;

        Assert.NotNull(synonyms);
        Assert.True(synonyms!.Contains("резерв"), "Ключ синонима 'резерв' отсутствует.");
    }
}
