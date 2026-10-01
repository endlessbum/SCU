using Xunit;

namespace SCU.Tests;

// Чтение поля Start из текстового бэкапа служб "name|start|state|delayed":
// по нему включение службы возвращается в исходный режим запуска.
public class ServiceManagerBackupTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"scu_svc_{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        File.Delete(_path);
    }

    [Fact]
    public void ReadStart_MissingFile_ReturnsNull()
    {
        Assert.Null(ServiceManager.ReadStartFromBackup(null, "wuauserv"));
        Assert.Null(ServiceManager.ReadStartFromBackup(@"Z:\нет\такого\файла.txt", "wuauserv"));
    }

    [Fact]
    public void ReadStart_FindsServiceCaseInsensitive()
    {
        File.WriteAllLines(_path, ["WSearch|3|Stopped|0", "wuauserv|2|Running|1"]);

        Assert.Equal(3, ServiceManager.ReadStartFromBackup(_path, "wsearch"));
        Assert.Equal(2, ServiceManager.ReadStartFromBackup(_path, "WUAUSERV"));
    }

    [Fact]
    public void ReadStart_TakesFirstMatch()
    {
        File.WriteAllLines(_path, ["svc|4|Running|0", "svc|2|Running|1"]);

        Assert.Equal(4, ServiceManager.ReadStartFromBackup(_path, "svc"));
    }

    [Fact]
    public void ReadStart_CorruptStartValue_ReturnsNull()
    {
        File.WriteAllLines(_path, ["svc|авто|Running|0", "другая|3|Stopped|0"]);

        Assert.Null(ServiceManager.ReadStartFromBackup(_path, "svc"));
        Assert.Null(ServiceManager.ReadStartFromBackup(_path, "отсутствующая"));
    }

    [Fact]
    public void ReadStart_LineWithTooFewFields_Ignored()
    {
        File.WriteAllLines(_path, ["svc"]);

        Assert.Null(ServiceManager.ReadStartFromBackup(_path, "svc"));
    }
}
