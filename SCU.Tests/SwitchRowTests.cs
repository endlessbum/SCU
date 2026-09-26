using System.ComponentModel;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

public class SwitchRowTests
{
    [Fact]
    public void ForceState_RaisesPropertyChanged_EvenWhenValueUnchanged()
    {
        // Контракт тумблеров: после операции тумблер синхронизируется с фактическим
        // состоянием, даже если оно совпало со старым — иначе «капля» залипает.
        var row = new SwitchRow("game-bar", "Game Bar", "описание", isOn: false);
        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        row.ForceState(false);

        Assert.Contains(nameof(SwitchRow.IsOn), notified);
        Assert.False(row.IsOn);
    }

    [Fact]
    public void SetIsOn_RaisesPropertyChanged()
    {
        var row = new SwitchRow("sticky-keys", "Залипание клавиш", string.Empty, isOn: false);
        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        row.IsOn = true;

        Assert.Contains(nameof(SwitchRow.IsOn), notified);
        Assert.True(row.IsOn);
    }

    [Fact]
    public void Constructor_StoresMetadata()
    {
        var row = new SwitchRow("mouse-acceleration", "Ускорение мыши", "описание", isOn: true);

        Assert.Equal("mouse-acceleration", row.Id);
        Assert.Equal("Ускорение мыши", row.Title);
        Assert.Equal("описание", row.Description);
        Assert.True(row.IsOn);
    }

    [Fact]
    public void ActionDescription_IsPermanent_DoesNotUseEnableDisablePrefix()
    {
        // План: под названием — постоянное описание, без «Включить:» / «Отключить:».
        var row = new SwitchRow("game-bar", "Game Bar", "Фоновая запись и оверлей.", isOn: false, onMeansEnable: true);

        Assert.Equal("Фоновая запись и оверлей.", row.ActionDescription);
        Assert.DoesNotContain("Включить:", row.ActionDescription);
        Assert.DoesNotContain("Отключить:", row.ActionDescription);

        row.IsOn = true;
        Assert.Equal("Фоновая запись и оверлей.", row.ActionDescription);
        Assert.DoesNotContain("Включить:", row.ActionDescription);
        Assert.DoesNotContain("Отключить:", row.ActionDescription);
    }

    [Fact]
    public void ActionDescription_EmptyDescription_IsEmpty()
    {
        var row = new SwitchRow("x", "Title", string.Empty, isOn: true);
        Assert.Equal(string.Empty, row.ActionDescription);
        Assert.False(row.HasActionDescription);
    }
}
