using SCU.Interop;
using Xunit;

namespace SCU.Tests;

public class LongProcessRunnerTests
{
    [Theory]
    [InlineData("GUID добавлен 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c (макс.)")]
    [InlineData("8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C")]
    public void ExtractGuids_FindsGuids_RegardlessOfCase(string output)
    {
        var guids = LongProcessRunner.ExtractGuids(output);

        var guid = Assert.Single(guids);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", guid.ToLowerInvariant());
    }

    [Fact]
    public void ExtractGuids_FindsMultipleGuids()
    {
        var output = "381b4222-f694-41f0-9685-ff5bb260df2e\n"
                     + "e9a42b02-d5df-448d-aa00-03f14749eb61";

        var guids = LongProcessRunner.ExtractGuids(output);

        Assert.Equal(2, guids.Count);
        Assert.Contains("381b4222-f694-41f0-9685-ff5bb260df2e", guids);
        Assert.Contains("e9a42b02-d5df-448d-aa00-03f14749eb61", guids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("нет GUID-ов здесь")]
    [InlineData("8c5e7fda-e8bf-4a96-9a85 — обрезанный GUID")]
    public void ExtractGuids_EmptyWhenNoFullGuid(string output)
    {
        Assert.Empty(LongProcessRunner.ExtractGuids(output));
    }

    [Fact]
    public void ExtractGuids_ExtractsNewPlanGuidFromDuplicateschemeOutput()
    {
        // Формат вывода powercfg -duplicatescheme: "ИД GUID: <guid> (имя)".
        var output = "GUID схемы электропитания: 11111111-2222-3333-4444-555555555555 (Копия)";

        var guid = LongProcessRunner.ExtractGuids(output).LastOrDefault();

        Assert.Equal("11111111-2222-3333-4444-555555555555", guid);
    }
}
