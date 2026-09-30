namespace SCU.AppCore.Help;

// Единый локальный каталог справки SCU (п. 4 ТЗ). Хранится и ищется локально:
// никаких внешних RAG/embeddings — weighted scoring по title/description/info/
// keywords с расширением запроса через синонимы DashboardSearchService
// (русско-английские стемы работают при любом языке интерфейса).
public interface IScuHelpService
{
    // Поиск по запросу пользователя: топ релевантных записей.
    IReadOnlyList<ScuHelpEntry> Search(string query, int maxResults = 5);

    // Точная запись по стабильному Id (для get_scu_help и HelpReference).
    ScuHelpEntry? Get(string id);

    // Весь индекс (для тестов и диагностики).
    IReadOnlyList<ScuHelpEntry> All { get; }
}
