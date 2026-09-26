using SCU.Services;
using Xunit;

namespace SCU.Tests;

// Парсер вывода списка запланированных задач (tab-separated: полный путь + состояние).
public class TaskManagerParseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"scu_tasks_{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        File.Delete(_path);
    }

    [Fact]
    public void ParseListFile_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(TaskManager.ParseListFile(Path.Combine(_path, "нет_такого.txt")));
    }

    [Fact]
    public void ParseListFile_ParsesPathAndState()
    {
        var line = @"\Microsoft\Windows\Update\Scheduled Start" + "\tГотово";
        File.WriteAllLines(_path, [line]);

        var items = TaskManager.ParseListFile(_path);

        var item = Assert.Single(items);
        Assert.Equal(@"\Microsoft\Windows\Update\Scheduled Start", item.FullPath);
        Assert.Equal("Scheduled Start", item.Name);
        Assert.Equal(@"\Microsoft\Windows\Update", item.Folder);
        Assert.Equal("Готово", item.State);
    }

    [Fact]
    public void ParseListFile_LineWithoutState_HasEmptyState()
    {
        File.WriteAllLines(_path, ["   " + @"\MyTask" + "   "]);

        var item = Assert.Single(TaskManager.ParseListFile(_path));

        Assert.Equal(@"\MyTask", item.FullPath);
        Assert.Equal("MyTask", item.Name);
        Assert.Equal(string.Empty, item.State);
    }

    [Fact]
    public void ParseListFile_SkipsBlankAndEmptyNameLines()
    {
        File.WriteAllLines(_path, ["", "\t\t", "  \tГотово"]);

        Assert.Empty(TaskManager.ParseListFile(_path));
    }
}
