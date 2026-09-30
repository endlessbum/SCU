namespace SCU.AppCore.Help;

// Запись локального каталога справки SCU (п. 4 ТЗ). Title/Description/InfoText —
// текст на текущем языке интерфейса (индекс пересобирается при смене языка),
// Keywords — сразу на русском и английском, чтобы поиск работал при любом
// языке UI. Id стабилен: на него ссылаются HelpReference в чате.
public sealed class ScuHelpEntry
{
    public required string Id { get; init; }

    public required int SectionNumber { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public string InfoText { get; init; } = string.Empty;

    public string Keywords { get; init; } = string.Empty;

    public string? RelatedUtilityId { get; init; }

    // Текст, по которому идёт поиск: все поля в одной строке (баллы считаются
    // по отдельным полям, а матчинг — по этому буферу).
    public string SearchableText => string.Join(' ', Title, Description, InfoText, Keywords);
}
