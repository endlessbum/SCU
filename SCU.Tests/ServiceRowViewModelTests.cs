using SCU.Models;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// Коллекция с последовательным выполнением: StateText/ActionCaption зависят от L.Current.
[Collection("LocalizationSequential")]
public class ServiceRowViewModelTests
{
    private static WindowsServiceInfo Info(
        ServiceStartupType startup = ServiceStartupType.Automatic,
        ServiceRuntimeStatus runtime = ServiceRuntimeStatus.Running,
        ServiceQueryStatus query = ServiceQueryStatus.Ok,
        string name = "wuauserv",
        bool delayed = false) =>
        new(name, "Windows Update", startup, runtime, delayed,
            IsStartAllowed: startup != ServiceStartupType.Disabled,
            query);

    [Fact]
    public void From_CopiesAllFields()
    {
        var row = ServiceRowViewModel.From(Info(delayed: true));

        Assert.Equal("wuauserv", row.Name);
        Assert.Equal("Windows Update", row.DisplayName);
        Assert.Equal(ServiceStartupType.Automatic, row.StartupType);
        Assert.Equal(ServiceRuntimeStatus.Running, row.RuntimeStatus);
        Assert.True(row.DelayedAutostart);
        Assert.Equal("да", row.DelayedAutostartText);
        Assert.True(row.IsServiceEnabled);
    }

    [Fact]
    public void CanToggle_FalseWhenNotReadable()
    {
        Assert.True(ServiceRowViewModel.From(Info()).CanToggle);
        Assert.False(ServiceRowViewModel.From(Info(query: ServiceQueryStatus.NotFound)).CanToggle);
        Assert.False(ServiceRowViewModel.From(Info(query: ServiceQueryStatus.AccessDenied)).CanToggle);
        Assert.False(ServiceRowViewModel.From(Info(query: ServiceQueryStatus.Unknown)).CanToggle);
    }

    [Fact]
    public void IsServiceEnabled_ReflectsStartupTypeNotRuntime()
    {
        // Automatic + Stopped → тумблер ON (запуск разрешён)
        Assert.True(ServiceRowViewModel.From(Info(
            ServiceStartupType.Automatic, ServiceRuntimeStatus.Stopped)).IsServiceEnabled);

        // Disabled + Running → тумблер OFF (запуск запрещён, даже если сейчас работает)
        Assert.False(ServiceRowViewModel.From(Info(
            ServiceStartupType.Disabled, ServiceRuntimeStatus.Running)).IsServiceEnabled);

        Assert.False(ServiceRowViewModel.From(Info(
            ServiceStartupType.Disabled, ServiceRuntimeStatus.Stopped)).IsServiceEnabled);
    }

    [Fact]
    public void RuntimeStatusText_ShowsPendingStates()
    {
        Assert.Equal("Запускается…",
            ServiceRowViewModel.From(Info(runtime: ServiceRuntimeStatus.StartPending)).RuntimeStatusText);
        Assert.Equal("Останавливается…",
            ServiceRowViewModel.From(Info(runtime: ServiceRuntimeStatus.StopPending)).RuntimeStatusText);
        Assert.Equal("Приостановлена",
            ServiceRowViewModel.From(Info(runtime: ServiceRuntimeStatus.Paused)).RuntimeStatusText);
    }

    [Fact]
    public void AccessDenied_NotShownAsMissing()
    {
        var row = ServiceRowViewModel.From(Info(query: ServiceQueryStatus.AccessDenied));
        Assert.Equal(ServiceQueryStatus.AccessDenied, row.QueryStatus);
        Assert.Contains("доступа", row.StateText, StringComparison.OrdinalIgnoreCase);
        Assert.False(row.CanToggle);
        Assert.False(row.IsServiceEnabled);
    }

    [Fact]
    public void NotFound_ShowsMissing()
    {
        var row = ServiceRowViewModel.From(Info(query: ServiceQueryStatus.NotFound));
        Assert.Equal(ServiceQueryStatus.NotFound, row.QueryStatus);
        Assert.False(row.CanToggle);
    }

    [Theory]
    [InlineData(ServiceRuntimeStatus.Running, "Работает")]
    [InlineData(ServiceRuntimeStatus.Stopped, "Остановлена")]
    [InlineData(ServiceRuntimeStatus.StartPending, "Запускается…")]
    [InlineData(ServiceRuntimeStatus.StopPending, "Останавливается…")]
    public void StateText_ReflectsRuntimeWhenOk(ServiceRuntimeStatus runtime, string expected)
    {
        Assert.Equal(expected, ServiceRowViewModel.From(Info(runtime: runtime)).StateText);
    }

    [Fact]
    public void StartupTypeText_Localized()
    {
        Assert.Equal("Автоматически (отложенный)",
            ServiceRowViewModel.From(Info(ServiceStartupType.AutomaticDelayed)).StartupTypeText);
        Assert.Equal("Вручную",
            ServiceRowViewModel.From(Info(ServiceStartupType.Manual)).StartupTypeText);
        Assert.Equal("Отключена",
            ServiceRowViewModel.From(Info(ServiceStartupType.Disabled)).StartupTypeText);
    }

    [Fact]
    public void ActionText_DoesNotContainEnableDisablePrefix()
    {
        var row = ServiceRowViewModel.From(Info(name: "SysMain"));
        Assert.False(string.IsNullOrWhiteSpace(row.ActionText));
        Assert.DoesNotContain("Включить:", row.ActionText);
        Assert.DoesNotContain("Отключить:", row.ActionText);
    }

    [Fact]
    public void ActionText_FallbackWhenNoDescription()
    {
        var row = ServiceRowViewModel.From(Info(name: "SomeUnknownSvc_XYZ"));
        Assert.Contains("отсутствует", row.ActionText, StringComparison.OrdinalIgnoreCase);
    }
}
