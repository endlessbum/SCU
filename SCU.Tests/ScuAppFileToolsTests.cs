using System.Text.Json;
using SCU.AppCore.AI;
using SCU.Models.AI;
using Xunit;

namespace SCU.Tests;

// Инструменты чтения файлов установки SCU (read-only, расширение ТЗ по запросу
// пользователя). Проверяются границы безопасности enforced в коде:
// - выход за корень (../, абсолютные пути) отвергается;
// - читаются только текстовые форматы из allowlist;
// - слишком большой файл не читается, длинный — обрезается с пометкой;
// - секрет в содержимом файла маскируется (SecretRedactor);
// - обычное чтение и listing работают.
public class ScuAppFileToolsTests : IDisposable
{
    private static readonly Logger Logger = Logger.CreateForCurrentRun();

    private readonly string _root;
    private readonly ScuListAppFilesTool _list;
    private readonly ScuReadAppFileTool _read;

    public ScuAppFileToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scu-ai-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "readme.txt"), "hello from scu");
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "settings.json"), """{"theme":"dark"}""");
        File.WriteAllBytes(Path.Combine(_root, "SCU.dll"), [1, 2, 3, 4]);

        _list = new ScuListAppFilesTool(_root);
        _read = new ScuReadAppFileTool(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ScuAiExecutionContext Context() => new()
    {
        Context = new ScuAiContext(
            "SCU", "0.0.0", "ru", false, 0, "Главная",
            null, null, "deepseek", "deepseek-chat", true),
    };

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static string ErrorOf(ScuAiToolResult result) =>
        result.ErrorMessage ?? string.Empty;

    [Fact]
    public async Task Read_AllowsRelativePathInsideRoot()
    {
        var result = await _read.ExecuteAsync(Args("""{"path":"readme.txt"}"""), Context());

        Assert.True(result.Success);
        Assert.Contains("hello from scu", result.DataJson);
    }

    [Fact]
    public async Task Read_AllowsSubfolderPath()
    {
        var result = await _read.ExecuteAsync(Args("""{"path":"config/settings.json"}"""), Context());

        Assert.True(result.Success);
        Assert.Contains("theme", result.DataJson);
    }

    [Fact]
    public async Task Read_RejectsParentDirectoryTraversal()
    {
        // Файл лежит вне корня — чтение через «..» должно быть отвергнуто.
        var outside = Path.Combine(Path.GetTempPath(), "scu-ai-outside.txt");
        File.WriteAllText(outside, "secret outside");

        var result = await _read.ExecuteAsync(Args("""{"path":"../scu-ai-outside.txt"}"""), Context());

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ValidationFailed, result.ErrorCode);
        Assert.DoesNotContain("secret outside", result.DataJson ?? string.Empty);
    }

    [Fact]
    public async Task Read_RejectsDeepTraversal()
    {
        var result = await _read.ExecuteAsync(
            Args("""{"path":"config/../../scu-ai-outside.txt"}"""), Context());

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ValidationFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Read_RejectsAbsolutePath()
    {
        // Абсолютный путь (пусть даже на существующий системный файл) отвергается
        // до всякой работы с диском.
        var result = await _read.ExecuteAsync(
            Args("""{"path":"C:/Windows/system.ini"}"""), Context());

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ValidationFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Read_RejectsBinaryExtension()
    {
        var result = await _read.ExecuteAsync(Args("""{"path":"SCU.dll"}"""), Context());

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ValidationFailed, result.ErrorCode);
        Assert.Contains(".dll", ErrorOf(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_TruncatesLongFileAndMarksIt()
    {
        var longPath = Path.Combine(_root, "long.log");
        File.WriteAllText(longPath, new string('a', 30_000));

        var result = await _read.ExecuteAsync(Args("""{"path":"long.log"}"""), Context());

        Assert.True(result.Success);
        Assert.Contains("\"truncated\":true", result.DataJson);
        // 30 000 символов не ушли модели целиком — только MaxReadChars.
        Assert.True(result.DataJson!.Length < 40_000);
    }

    [Fact]
    public async Task Read_MasksSecretsInFileContent()
    {
        File.WriteAllText(Path.Combine(_root, "leaked.log"), "api_key = supersecret123456");

        var result = await _read.ExecuteAsync(Args("""{"path":"leaked.log"}"""), Context());

        Assert.True(result.Success);
        Assert.DoesNotContain("supersecret123456", result.DataJson);
        Assert.Contains("[REDACTED]", result.DataJson);
    }

    [Fact]
    public async Task Read_MissingFile_FailsWithoutFakeSuccess()
    {
        var result = await _read.ExecuteAsync(Args("""{"path":"no-such-file.txt"}"""), Context());

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ValidationFailed, result.ErrorCode);
    }

    [Fact]
    public async Task List_ReturnsRelativePathsWithSizes()
    {
        var result = await _list.ExecuteAsync(Args("{}"), Context());

        Assert.True(result.Success);
        Assert.Contains("readme.txt", result.DataJson);
        Assert.Contains("settings.json", result.DataJson);
        Assert.Contains("SCU.dll", result.DataJson);
        Assert.Contains("\"truncated\":false", result.DataJson);
    }

    [Fact]
    public async Task List_RespectsSearchPattern()
    {
        var result = await _list.ExecuteAsync(Args("""{"search_pattern":"*.json"}"""), Context());

        Assert.True(result.Success);
        Assert.Contains("settings.json", result.DataJson);
        Assert.DoesNotContain("readme.txt", result.DataJson);
    }

    [Fact]
    public async Task List_MissingSubfolder_Fails()
    {
        var result = await _list.ExecuteAsync(Args("""{"subfolder":"no-such-dir"}"""), Context());

        Assert.False(result.Success);
        Assert.Equal(ScuAiErrorCode.ValidationFailed, result.ErrorCode);
    }
}
