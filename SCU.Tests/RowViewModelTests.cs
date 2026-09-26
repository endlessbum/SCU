using SCU.Models;
using SCU.Services;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

[Collection("LocalizationSequential")]
public class BloatRowTests
{
    private static BloatRow CreateRow(string stateText) =>
        new(new BloatApp("Xbox", "Xbox", " Xbox, GamingApp"), stateText);

    private static List<string> Track(BloatRow row)
    {
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        return raised;
    }

    [Fact]
    public void UnknownState_IsUnknown_CannotRemove()
    {
        var row = CreateRow("неизвестно — выполните скан");

        Assert.True(row.IsUnknown);
        Assert.False(row.CanRemove);
        Assert.False(row.IsRemoved);
    }

    [Fact]
    public void InstalledState_CanRemove()
    {
        var row = CreateRow("установлено");

        Assert.True(row.CanRemove);
        Assert.False(row.IsRemoved);
        Assert.False(row.IsUnknown);
    }

    [Theory]
    [InlineData("отсутствует")]
    [InlineData("пакеты удалены (проверено скриптом)")]
    public void RemovedStates_ShowCheckmark(string state)
    {
        var row = CreateRow(state);

        Assert.True(row.IsRemoved);
        Assert.False(row.CanRemove);
    }

    [Fact]
    public void StateText_Setter_RaisesAllDependentProperties()
    {
        var row = CreateRow("неизвестно — выполните скан");
        var raised = Track(row);

        row.StateText = "установлено";

        Assert.Equal(new[]
        {
            nameof(BloatRow.StateText),
            nameof(BloatRow.DisplayText),
            nameof(BloatRow.CanRemove),
            nameof(BloatRow.IsRemoved),
            nameof(BloatRow.IsUnknown)
        }, raised);
        Assert.True(row.CanRemove);
    }

    [Fact]
    public void DisplayText_TranslatesStateForUi()
    {
        // Логика держится на канонических русских ключах, экран получает перевод.
        var row = CreateRow("установлено");
        var saved = SCU.Common.L.Current;
        try
        {
            SCU.Common.L.SetLanguage(SCU.Common.AppLanguage.En, notify: false);
            Assert.Equal("installed", row.DisplayText);
        }
        finally
        {
            SCU.Common.L.SetLanguage(saved, notify: false);
        }
    }

    [Fact]
    public void Row_PassesThroughAppMetadata()
    {
        var app = new BloatApp("Edge", "Microsoft Edge", "setup.exe", Irreversible: true);
        var row = new BloatRow(app, "установлено");

        Assert.Equal("Edge", row.ScanKey);
        Assert.Equal("Microsoft Edge", row.Title);
        Assert.Equal("setup.exe", row.RemoveNames);
        Assert.True(row.Irreversible);
    }
}

public class ComponentItemTests
{
    [Fact]
    public void IsInstalled_SameValue_RaisesNothing()
    {
        var item = new ComponentItem { Id = ".NET", Name = ".NET Runtime", IsInstalled = true };
        var raised = 0;
        item.PropertyChanged += (_, _) => raised++;

        item.IsInstalled = true;

        Assert.Equal(0, raised);
    }

    [Fact]
    public void IsInstalled_ChangedValue_RaisesOnce()
    {
        var item = new ComponentItem();
        var names = new List<string?>();
        item.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        item.IsInstalled = true;

        Assert.Single(names);
        Assert.Equal(nameof(ComponentItem.IsInstalled), names[0]);
        Assert.True(item.IsInstalled);
    }
}
