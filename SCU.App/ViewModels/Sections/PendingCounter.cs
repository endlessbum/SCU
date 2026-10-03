using SCU.Common;

namespace SCU.ViewModels.Sections;

// Счётчик «К применению: N» на «Главной». Вынесен из DashboardViewModel для
// тестируемости: разделы инициализируются лениво и один раз, поэтому признак
// «раздел уже читал систему» подаётся снаружи (seam isSectionLoaded).
internal static class PendingCounter
{
    // Сколько выбранных утилит ещё не в целевом состоянии. Утилиты разделов,
    // которые ещё не инициализировались, НЕ считаются: их NeedsApply видит
    // конструкторские дефолты (IsOn=false и т.п.) и дал бы ложное «ещё не
    // применено». После догрузки раздела счётчик пересчитывается.
    public static int Count(
        IEnumerable<(bool IsIncluded, int Section, Func<bool?>? NeedsApply)> utilities,
        Func<int, bool> isSectionLoaded) =>
        utilities.Count(utility =>
            utility.IsIncluded
            && isSectionLoaded(utility.Section)
            && utility.NeedsApply?.Invoke() == true);
}
