using Xunit;

namespace SCU.Tests;

public class AppxScanStateTests
{
    private static AppxScanState Create(bool scanned, params (string Key, string Value)[] values) =>
        new(scanned, values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void IsPresent_TrueOnlyForValueOne()
    {
        var state = Create(true, ("Xbox", "1"), ("Cam", "0"), ("Clip", "unknown"));

        Assert.True(state.IsPresent("Xbox"));
        Assert.False(state.IsPresent("Cam"));
        Assert.False(state.IsPresent("Clip"));
    }

    [Fact]
    public void IsPresent_UnknownKey_ReturnsFalse()
    {
        var state = Create(true, ("Cam", "0"));

        Assert.False(state.IsPresent("Xbox"));
    }

    [Fact]
    public void StateText_WhenScanFailed_ShowsUnknown()
    {
        var state = Create(false, ("Xbox", "unknown"));

        Assert.Equal("неизвестно (скан не удался)", state.StateText("Xbox"));
    }

    [Fact]
    public void StateText_ReflectsValue()
    {
        var state = Create(true, ("Xbox", "1"), ("Cam", "0"));

        Assert.Equal("установлено", state.StateText("Xbox"));
        Assert.Equal("отсутствует", state.StateText("Cam"));
    }

    [Fact]
    public void StateText_UnknownKey_ShowsUnknown()
    {
        var state = Create(true);

        Assert.Equal("неизвестно (скан не удался)", state.StateText("Edge"));
    }

    [Fact]
    public void LeftoverNames_ReadsKeyLeftLine()
    {
        var state = Create(true, ("Xbox", "1"), ("XboxLeft", "Microsoft.XboxGamingOverlay, Microsoft.Xbox.TCUI"));

        var leftovers = state.LeftoverNames("Xbox");

        Assert.Equal(2, leftovers.Count);
        Assert.Equal("Microsoft.XboxGamingOverlay", leftovers[0]);
        Assert.Equal("Microsoft.Xbox.TCUI", leftovers[1]);
    }

    [Fact]
    public void LeftoverNames_MissingOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(Create(true, ("Xbox", "0")).LeftoverNames("Xbox"));
        Assert.Empty(Create(true, ("Xbox", "1"), ("XboxLeft", " ")).LeftoverNames("Xbox"));
    }
}
