namespace SCU.Models;

// Серьёзность рекомендации. Info — осознанные настройки и наблюдения,
// Warning/Critical — то, что действительно требует внимания.
public enum RecommendationSeverity
{
    Info,
    Warning,
    Critical
}

// Одна рекомендация MVP: факт из снимка + объяснимое правило. Id — стабильный ключ
// (используется тестами), тексты — русские ключи L.T с переводами в Localization.cs.
public sealed record Recommendation(
    string Id,
    string Category,
    RecommendationSeverity Severity,
    string Title,
    string Description,
    string ActionText,
    int? NavigationSectionNumber);
