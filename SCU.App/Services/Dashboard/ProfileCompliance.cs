using SCU.Models;

namespace SCU.Services.Dashboard;

// Чистая оценка соответствия шагов профилю (без обращения к системе): вход — каталог
// шагов, целевой словарь профиля и фактически прочитанные состояния. Тестируемость —
// главная причина вынесения: движок соответствия не зависит от сервисов.
public static class ProfileCompliance
{
    // Перебор идёт по каталогу: в оценку попадают только реально существующие шаги,
    // а порядок строк стабилен (порядок каталога). Шаги цели, которых нет в каталоге,
    // игнорируются — целевой словарь мог остаться от старой версии приложения.
    public static ProfileComplianceResult Evaluate(
        IReadOnlyList<ProfileStep> catalog,
        IReadOnlyDictionary<string, StepDesire> target,
        IReadOnlyDictionary<string, bool?> actualStates)
    {
        var entries = new List<ProfileComplianceEntry>();
        var matched = 0;
        foreach (var step in catalog)
        {
            if (!target.TryGetValue(step.Id, out var desired))
            {
                continue;
            }

            var actual = actualStates.TryGetValue(step.Id, out var state) ? state : null;
            // null (прочитать не удалось) — несовпадение: unknown не засчитывается.
            var matches = actual is { } value && value == (desired == StepDesire.On);
            if (matches)
            {
                matched++;
            }

            entries.Add(new ProfileComplianceEntry(step.Id, desired, actual, matches));
        }

        return new ProfileComplianceResult(entries, matched, entries.Count);
    }
}
