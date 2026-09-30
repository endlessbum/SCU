using SCU.ViewModels.Sections;

namespace SCU.AppCore.Help;

// Локальный поиск по индексу справки (п. 4 ТЗ): weighted scoring с раскрытием
// запроса через синонимы DashboardSearchService (те же RU/EN-стемы, что у
// глобального поиска SCU). Индекс строится один раз и пересобирается только
// при смене языка интерфейса — полный обход по проекту на каждый запрос не делается.
public sealed class ScuHelpService : IScuHelpService
{
    private readonly Func<IReadOnlyList<ScuHelpEntry>> _indexFactory;
    private IReadOnlyList<ScuHelpEntry> _index;

    public ScuHelpService(Func<IReadOnlyList<ScuHelpEntry>> indexFactory)
    {
        _indexFactory = indexFactory;
        _index = indexFactory();

        // Тексты записей меняются при смене языка — пересобираем индекс.
        // Событие живёт столько же, сколько приложение (как у SectionItem).
        L.LanguageChanged += RefreshIndex;
    }

    public IReadOnlyList<ScuHelpEntry> All => _index;

    public ScuHelpEntry? Get(string id) =>
        _index.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

    // Смена языка интерфейса: тексты разделов/утилит берутся из ресурсных
    // словарей, поэтому индекс пересобирается целиком (п. 20 ТЗ).
    private void RefreshIndex() => _index = _indexFactory();

    // Вес точки совпадения: заголовок важнее описания, keywords — посередине.
    private const int TitleWeight = 10;
    private const int KeywordsWeight = 6;
    private const int DescriptionWeight = 4;
    private const int AllTokensBonus = 5;

    public IReadOnlyList<ScuHelpEntry> Search(string query, int maxResults = 5)
    {
        var tokens = DashboardSearchService.SplitTokens(query);
        if (tokens.Length == 0)
        {
            return [];
        }

        var normalizedQuery = Normalize(query);
        var scored = new List<(ScuHelpEntry Entry, int Score)>();
        foreach (var entry in _index)
        {
            var title = Normalize(entry.Title);
            var description = Normalize(entry.Description);
            var info = Normalize(entry.InfoText);
            var keywords = Normalize(entry.Keywords);

            var score = 0;
            var allTokens = true;
            foreach (var token in tokens)
            {
                var inTitle = DashboardSearchService.MatchesToken(token, title);
                var inKeywords = !inTitle && DashboardSearchService.MatchesToken(token, keywords);
                var inDescription = !inTitle && !inKeywords && DashboardSearchService.MatchesToken(token, description + " " + info);
                if (!inTitle && !inKeywords && !inDescription)
                {
                    allTokens = false;
                    continue;
                }

                score += inTitle ? TitleWeight : inKeywords ? KeywordsWeight : DescriptionWeight;
            }

            // Точная фраза в заголовке — самый сильный сигнал.
            if (normalizedQuery.Length > 2 && title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score += TitleWeight;
            }

            if (allTokens && score > 0)
            {
                score += AllTokensBonus;
            }

            if (score > 0)
            {
                scored.Add((entry, score));
            }
        }

        return scored
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Entry.Title, StringComparer.CurrentCulture)
            .Take(maxResults)
            .Select(pair => pair.Entry)
            .ToList();
    }

    // Нормализация для фразового матчинга: нижний регистр, обрезка концов.
    private static string Normalize(string text) => text.Trim().ToLowerInvariant();
}
