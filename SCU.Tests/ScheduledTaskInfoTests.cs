using SCU.Models;
using Xunit;

namespace SCU.Tests;

// Коллекция с последовательным выполнением: LocalizedState зависит от L.Current.
[Collection("LocalizationSequential")]
public class ScheduledTaskInfoTests
{
    [Theory]
    [InlineData("\\Microsoft\\Windows\\UpdateOrchestrator\\UsbUwp", "UsbUwp", "\\Microsoft\\Windows\\UpdateOrchestrator")]
    [InlineData("\\Task", "Task", "\\")]
    public void NameAndFolder_SplitFullPath(string fullPath, string expectedName, string expectedFolder)
    {
        var info = new ScheduledTaskInfo(fullPath, "Ready");

        Assert.Equal(expectedName, info.Name);
        Assert.Equal(expectedFolder, info.Folder);
    }

    [Fact]
    public void Name_PathWithoutBackslash_ReturnsFullPath()
    {
        var info = new ScheduledTaskInfo("Task", "Ready");

        Assert.Equal("Task", info.Name);
        Assert.Equal("\\", info.Folder);
    }

    [Fact]
    public void IsMissing_MatchesCaseInsensitively()
    {
        Assert.True(new ScheduledTaskInfo("\\X\\Y", "MISSING").IsMissing);
        Assert.True(new ScheduledTaskInfo("\\X\\Y", "missing").IsMissing);
        Assert.False(new ScheduledTaskInfo("\\X\\Y", "Ready").IsMissing);
    }

    [Fact]
    public void IsDisabled_MatchesExactState()
    {
        Assert.True(new ScheduledTaskInfo("\\X\\Y", "Disabled").IsDisabled);
        // Планировщик возвращает состояние в верхнем регистре — тоже считается отключённой.
        Assert.True(new ScheduledTaskInfo("\\X\\Y", "DISABLED").IsDisabled);
        Assert.False(new ScheduledTaskInfo("\\X\\Y", "Ready").IsDisabled);
    }

    [Fact]
    public void CanDisable_FalseForMissingOrDisabled()
    {
        Assert.False(new ScheduledTaskInfo("\\X\\Y", "MISSING").CanDisable);
        Assert.False(new ScheduledTaskInfo("\\X\\Y", "Disabled").CanDisable);
        Assert.True(new ScheduledTaskInfo("\\X\\Y", "Ready").CanDisable);
    }

    [Theory]
    [InlineData("MISSING", "Отключить")] // П.18: «Нет задачи» на кнопке больше не показывается
    [InlineData("Disabled", "Отключена")]
    [InlineData("Ready", "Отключить")]
    public void ActionCaption_ReflectsState(string state, string expected)
    {
        Assert.Equal(expected, new ScheduledTaskInfo("\\X\\Y", state).ActionCaption);
    }

    [Fact]
    public void ActionCaption_MissingTask_CaptionSameButCannotDisable()
    {
        // П.18: у отсутствующей задачи кнопка «Отключить» неактивна.
        var missing = new ScheduledTaskInfo("\\X\\Y", "MISSING");
        Assert.Equal("Отключить", missing.ActionCaption);
        Assert.False(missing.CanDisable);
        Assert.True(new ScheduledTaskInfo("\\X\\Y", "Ready").CanDisable);
    }

    [Theory]
    [InlineData("Ready", "Готова")]
    [InlineData("Running", "Выполняется")]
    [InlineData("Disabled", "Отключена")]
    [InlineData("MISSING", "Нет задачи")]
    public void LocalizedState_TranslatesParserStates(string state, string expected)
    {
        Assert.Equal(expected, new ScheduledTaskInfo("\\X\\Y", state).LocalizedState);
    }

    [Fact]
    public void LocalizedState_UnknownState_PassesThroughRaw()
    {
        // Логика остаётся на сыром State; экран показывает его как есть, если перевода нет.
        Assert.Equal("Queued", new ScheduledTaskInfo("\\X\\Y", "Queued").LocalizedState);
    }

    [Fact]
    public void LocalizedState_EnglishMode_TranslatesStates()
    {
        var info = new ScheduledTaskInfo("\\X\\Y", "Ready");
        var saved = SCU.Common.L.Current;
        try
        {
            SCU.Common.L.SetLanguage(SCU.Common.AppLanguage.En, notify: false);
            Assert.Equal("Ready", info.LocalizedState);
            Assert.Equal("Running", new ScheduledTaskInfo("\\X\\Y", "Running").LocalizedState);
            Assert.Equal("No task", new ScheduledTaskInfo("\\X\\Y", "MISSING").LocalizedState);
        }
        finally
        {
            SCU.Common.L.SetLanguage(saved, notify: false);
        }
    }
}
