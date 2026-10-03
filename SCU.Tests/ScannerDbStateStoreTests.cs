using SCU.Infrastructure.Logging;
using SCU.Infrastructure.Networking;
using SCU.Infrastructure.Storage;
using Xunit;

namespace SCU.Tests;

// Стор версии установленной базы сканера: roundtrip, отсутствие файла и
// повреждённый файл дают null (скачивание пакета не блокируется).
public sealed class ScannerDbStateStoreTests : IDisposable
{
    private readonly string _directory;

    public ScannerDbStateStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временная папка не критична.
        }
    }

    private ScannerDbStateStore CreateStore() => new(
        Logger.CreateForCurrentRun(),
        Path.Combine(_directory, "scanner-db-state.json"));

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(CreateStore().Load());
    }

    [Fact]
    public void SaveLoad_Roundtrip()
    {
        var store = CreateStore();
        store.Save("2026.10.01");

        Assert.Equal("2026.10.01", CreateStore().Load());
    }

    [Fact]
    public void Save_OverwritesPreviousVersion()
    {
        var store = CreateStore();
        store.Save("2026.10.01");
        store.Save("2026.10.02");

        Assert.Equal("2026.10.02", store.Load());
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsNull()
    {
        var path = Path.Combine(_directory, "scanner-db-state.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(CreateStore().Load());
    }

    [Fact]
    public void Load_EmptyVersion_ReturnsNull()
    {
        var path = Path.Combine(_directory, "scanner-db-state.json");
        File.WriteAllText(path, "{\"version\":\"\"}");

        Assert.Null(CreateStore().Load());
    }
}

// Вывод адреса db-version.json из URL пакета: тот же каталог, имя файла
// заменяется. Ошибка разбора адреса — null (сверка просто не выполняется).
public sealed class ScannerDbVersionUrlTests
{
    [Fact]
    public void DeriveVersionUrl_FromDefaultPackageUrl()
    {
        Assert.Equal(
            "https://github.com/endlessbum/SCU/releases/download/scanner-db/db-version.json",
            ScannerUpdateService.DeriveVersionUrl(ScannerUpdateService.DefaultUrl));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void DeriveVersionUrl_UnparsableUrl_ReturnsNull(string url)
    {
        Assert.Null(ScannerUpdateService.DeriveVersionUrl(url));
    }
}
