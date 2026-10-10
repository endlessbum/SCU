using SCU.Infrastructure.Logging;
using SCU.Infrastructure.Storage;
using Xunit;

namespace SCU.Tests;

// Forward-compatibility state-файла (аудит storage): файл, записанный более
// новой схемой, читается, но не перезаписывается — иначе старый билд молча
// терял бы неизвестные поля, перезаписывая конверт с локальной версией.
public sealed class JsonStateFileSchemaTests : IDisposable
{
    private readonly string _directory;

    public JsonStateFileSchemaTests()
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

    private string FilePath => Path.Combine(_directory, "state.json");

    private JsonStateFile<string> CreateStore() =>
        new(Logger.CreateForCurrentRun(), FilePath, capacity: 10, label: "TEST");

    [Fact]
    public async Task FutureSchema_LoadAll_ReturnsItems()
    {
        // Файл «будущей версии приложения»: та же схема имён (PascalCase),
        // повышенный version и новое поле — как писал бы реальный билд v2.
        await File.WriteAllTextAsync(FilePath,
            """{"Version": 2, "Items": ["a", "b"], "FutureField": true}""");

        var items = await CreateStore().LoadAllAsync();

        Assert.Equal(["a", "b"], items);
    }

    [Fact]
    public async Task FutureSchema_Append_DoesNotRewriteFile()
    {
        var original = """{"Version": 2, "Items": ["a"], "FutureField": true}""";
        await File.WriteAllTextAsync(FilePath, original);

        var store = CreateStore();
        await store.LoadAllAsync();
        await store.AppendAsync("b");

        // Файл не перезаписан конвертом версии 1: неизвестные поля живы.
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task CurrentSchema_Append_RewritesFile()
    {
        // Контрольный случай: обычный файл v1 пишется как раньше.
        var store = CreateStore();
        var saved = await store.AppendAsync("a");

        Assert.True(saved);
        Assert.Equal(["a"], await CreateStore().LoadAllAsync());
    }

    // Аудит persistence: сбой записи не скрывается — AppendAsync возвращает
    // false, вызывающий код может сообщить о потере записи.
    [Fact]
    public async Task AppendAsync_WriteFailure_ReturnsFalse()
    {
        // Приём: целевой путь — каталог. Temp-файл создаётся, а File.Move
        // поверх каталога падает с IOException.
        var blockedPath = Path.Combine(_directory, "blocked");
        Directory.CreateDirectory(blockedPath);
        var store = new JsonStateFile<string>(
            Logger.CreateForCurrentRun(), blockedPath, capacity: 10, label: "TEST");

        var saved = await store.AppendAsync("a");

        Assert.False(saved);
    }

    [Fact]
    public async Task ClearAsync_MissingFile_ReturnsTrue()
    {
        var saved = await CreateStore().ClearAsync();

        Assert.True(saved);
    }
}
