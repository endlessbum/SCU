using SCU.Common;
using SCU.Infrastructure.Logging;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// Привязка скрепок к карточкам ввода (CanPin): ровно 5 тумблеров из ТЗ получают
// PinCardId («input.<id>»), «Ускорение запуска Edge» — нет. Конструктор
// InputViewModel не делает IO (реестр читается позже, в InitializeAsync).
public sealed class InputPinCardTests
{
    private sealed class NoDialogs : IConfirmDialogService
    {
        public bool Ask(string title, string message, string? confirmText = null) => false;
    }

    private static InputViewModel CreateViewModel() =>
        new(Logger.CreateForCurrentRun(), new NoDialogs());

    [Fact]
    public void Rows_SixSwitches_ExactlyFivePinnable()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(6, viewModel.Rows.Count);
        Assert.Equal(5, viewModel.Rows.Count(r => !string.IsNullOrEmpty(r.PinCardId)));
    }

    [Fact]
    public void PinnableRows_MatchTaskSwitchesWithInputPrefix()
    {
        // ТЗ «Вкладка „Ввод“»: ускорение мыши, залипание клавиш, Game Bar,
        // фоновая запись и DVR, Game Mode.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "input.mouse-acceleration",
            "input.sticky-keys",
            "input.game-bar",
            "input.game-dvr",
            "input.game-mode",
        };

        var actual = CreateViewModel().Rows
            .Where(r => !string.IsNullOrEmpty(r.PinCardId))
            .Select(r => r.PinCardId)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EdgeStartupBoost_IsNotPinnable()
    {
        var row = CreateViewModel().Rows.Single(r => r.Id == "edge-startup-boost");

        Assert.Empty(row.PinCardId);
    }
}
