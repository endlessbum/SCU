using SCU.Infrastructure.Windows;
using Xunit;

namespace SCU.Tests;

// Команда автозапуска: «--tray» обязан стоять ПОСЛЕ пути приложения. Для
// dotnet-режима путь к entry assembly идёт перед аргументами — CLI трактует
// «--tray» до пути как свою опцию и автозапуск падал молча.
public sealed class ScuAutorunTests
{
    [Fact]
    public void BuildCommand_RegularExe_AppendsTrayArg()
    {
        var command = ScuAutorun.BuildCommand(@"C:\Program Files\SCU\SCU.exe");

        Assert.Equal("\"C:\\Program Files\\SCU\\SCU.exe\" --tray", command);
    }

    [Fact]
    public void BuildCommand_Dotnet_PutsEntryAssemblyBeforeTrayArg()
    {
        // В тест-хосте entry assembly существует: проверяем порядок
        // «dotnet.exe → dll → --tray».
        var command = ScuAutorun.BuildCommand(@"C:\Program Files\dotnet\dotnet.exe");

        Assert.NotNull(command);
        Assert.EndsWith(" --tray", command);
        Assert.StartsWith("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"", command);
        Assert.Contains(".dll\" --tray", command);
    }

    [Fact]
    public void BuildCommand_EmptyPath_ReturnsNull()
    {
        Assert.Null(ScuAutorun.BuildCommand(string.Empty));
    }
}
