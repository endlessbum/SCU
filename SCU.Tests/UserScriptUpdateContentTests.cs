using System.Text;
using SCU.Infrastructure.Logging;
using SCU.Infrastructure.Storage;
using Xunit;

namespace SCU.Tests;

// Аудит HIGH: UpdateContent обязан быть транзакционным — при любом сбое
// metadata (SHA-256 в scripts.json и in-memory) и целевой файл остаются
// согласованными, иначе скрипт навсегда блокируется проверкой целостности.
public sealed class UserScriptUpdateContentTests : IDisposable
{
    private readonly string _directory;

    public UserScriptUpdateContentTests()
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

    private UserScriptStore CreateStore() => new(Logger.CreateForCurrentRun(), _directory);

    private UserScriptData CreateData(string content) =>
        new()
        {
            Id = "audit-script",
            Title = "Скрипт аудита",
            Extension = ".cmd",
            Sha256 = string.Empty, // TOFU на первом прогоне
        };

    [Fact]
    public void UpdateContent_HappyPath_FileAndMetadataAgree()
    {
        var store = CreateStore();
        var data = CreateData("old");
        File.WriteAllText(store.ScriptPath(data), "old content");
        data.Sha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("old content")));

        var ok = store.UpdateContent(data, "new content");

        Assert.True(ok);
        Assert.Equal("new content", File.ReadAllText(store.ScriptPath(data)));
        var expectedSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("new content")));
        Assert.Equal(expectedSha, data.Sha256);
        // В scripts.json тот же SHA.
        Assert.Contains(expectedSha[..16], File.ReadAllText(Path.Combine(_directory, "scripts.json")));
    }

    [Fact]
    public void UpdateContent_MoveFails_MetadataAndTargetUntouched()
    {
        var store = CreateStore();
        var data = CreateData("old");
        var targetPath = store.ScriptPath(data);
        File.WriteAllText(targetPath, "old content");
        var oldSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("old content")));
        data.Sha256 = oldSha;

        // Failure injection: каталог с именем target+".new" → WriteAllText темпа
        // падает, Move не выполняется.
        Directory.CreateDirectory(targetPath + ".new");

        var ok = store.UpdateContent(data, "new content");

        Assert.False(ok);
        Assert.Equal("old content", File.ReadAllText(targetPath));
        Assert.Equal(oldSha, data.Sha256);
        // Сбой произошёл до записи metadata — scripts.json не создан.
        Assert.False(File.Exists(Path.Combine(_directory, "scripts.json")));
    }
}
