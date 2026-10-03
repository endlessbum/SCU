using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// Счётчик «К применению: N» на «Главной»: утилиты неинициализированных
// разделов не считаются (их состояния — конструкторские дефолты, NeedsApply
// дал бы ложное «ещё не применено»), кнопки без NeedsApply — тоже.
public sealed class PendingCounterTests
{
    private static (bool IsIncluded, int Section, Func<bool?>? NeedsApply) Row(
        bool isIncluded, int section, Func<bool?>? needsApply) =>
        (isIncluded, section, needsApply);

    [Fact]
    public void Count_IncludedSectionLoadedNeedsApplyTrue_Counted()
    {
        var count = PendingCounter.Count(
        [
            Row(isIncluded: true, section: 8, needsApply: () => true),
            Row(isIncluded: true, section: 9, needsApply: () => true),
        ], isSectionLoaded: _ => true);

        Assert.Equal(2, count);
    }

    [Fact]
    public void Count_SectionNotLoaded_SkippedEvenIfNeedsApplyTrue()
    {
        // Раздел 11 ещё не инициализировался: его «NeedsApply=true» — артефакт
        // дефолта IsOn, а не факт системы.
        var count = PendingCounter.Count(
        [
            Row(isIncluded: true, section: 8, needsApply: () => true),
            Row(isIncluded: true, section: 11, needsApply: () => true),
        ], isSectionLoaded: section => section != 11);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Count_ExcludedOrAppliedOrButtons_NotCounted()
    {
        var count = PendingCounter.Count(
        [
            Row(isIncluded: false, section: 8, needsApply: () => true),  // не выбран
            Row(isIncluded: true, section: 8, needsApply: () => false),  // уже применено
            Row(isIncluded: true, section: 8, needsApply: null),         // кнопка-операция
            Row(isIncluded: true, section: 8, needsApply: () => null),   // проверить нельзя
        ], isSectionLoaded: _ => true);

        Assert.Equal(0, count);
    }
}
