using SCU.Common;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

public sealed class FakeConfirmDialogService : IConfirmDialogService
{
    public bool Ask(string title, string message, string? confirmText = null) => true;
}

public sealed class InputViewModelSmokeTests
{
    [Fact]
    public void CanConstructInputViewModel()
    {
        var vm = new InputViewModel(SCU.Infrastructure.Logging.Logger.CreateForCurrentRun(), new FakeConfirmDialogService());
        Assert.NotNull(vm);
    }
}
