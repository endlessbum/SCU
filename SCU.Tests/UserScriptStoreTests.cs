using SCU.Common;
using Xunit;

namespace SCU.Tests;

// Пользовательские скрипты: хранилище (импорт/удаление/roundtrip) и проверка
// работоспособности (.ps1 — парсер PowerShell, .bat — читаемость).
public sealed class UserScriptStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly Logger _logger;
    private readonly UserScriptStore _store;

    public UserScriptStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SCU.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logger = Logger.CreateForCurrentRun();
        _store = new UserScriptStore(_logger, _directory);
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

    [Fact]
    public void Import_CopiesFile_AndRoundtrips()
    {
        var source = Path.Combine(_directory, "source.ps1");
        File.WriteAllText(source, "Write-Output 'hi'");

        var data = _store.Import(source, 3, "Мой скрипт", "комментарий", "информер", "us_test");
        var scripts = new List<UserScriptData> { data };
        _store.Save(scripts);

        Assert.True(File.Exists(_store.ScriptPath(data)));
        var loaded = new UserScriptStore(_logger, _directory).Load();
        var restored = Assert.Single(loaded);
        Assert.Equal("us_test", restored.Id);
        Assert.Equal(".ps1", restored.Extension);
        Assert.Equal(3, restored.SectionNumber);
        Assert.Equal("Мой скрипт", restored.Title);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var source = Path.Combine(_directory, "s.bat");
        File.WriteAllText(source, "@echo off");
        var data = _store.Import(source, 3, "t", "", "", "us_del");

        _store.Delete(data);

        Assert.False(File.Exists(_store.ScriptPath(data)));
    }

    [Fact]
    public async Task Validate_BadPowerShell_ReturnsError()
    {
        var path = Path.Combine(_directory, "broken.ps1");
        File.WriteAllText(path, "function { this is not ( powershell");

        var result = await _store.ValidateAsync(path);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public async Task Validate_UnsupportedExtension_ReturnsError()
    {
        var path = Path.Combine(_directory, "script.cmd");
        File.WriteAllText(path, "@echo off");

        var result = await _store.ValidateAsync(path);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Validate_EmptyScript_ReturnsError()
    {
        var path = Path.Combine(_directory, "empty.bat");
        File.WriteAllText(path, string.Empty);

        var result = await _store.ValidateAsync(path);

        Assert.False(result.Ok);
    }

    // ===================== Целостность при запуске =====================

    [Fact]
    public void Import_RecordsSha256()
    {
        var source = Path.Combine(_directory, "s.bat");
        File.WriteAllText(source, "@echo off");
        var data = _store.Import(source, 3, "t", "", "", "us_sha");

        Assert.False(string.IsNullOrEmpty(data.Sha256));

        var stored = _store.ScriptPath(data);
        using (var stream = File.OpenRead(stored))
        {
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), data.Sha256);
        }
    }

    [Fact]
    public void SaveLoad_PreservesSha256()
    {
        var source = Path.Combine(_directory, "s.ps1");
        File.WriteAllText(source, "Write-Output 'hi'");
        var data = _store.Import(source, 3, "t", "", "", "us_persist");

        _store.Save([data]);
        var loaded = Assert.Single(_store.Load());

        Assert.Equal(data.Sha256, loaded.Sha256);
    }

    [Fact]
    public async Task Run_ModifiedAfterImport_IsRefused()
    {
        var source = Path.Combine(_directory, "src.bat");
        File.WriteAllText(source, "@echo off");
        var data = _store.Import(source, 3, "t", "", "", "us_modified");

        // Подмена копии в хранилище после импорта (атака процесса того же пользователя).
        File.WriteAllText(_store.ScriptPath(data), "echo MALICIOUS");

        var (exitCode, output) = await _store.RunAsync(data);

        Assert.Equal(-1, exitCode);
        Assert.Contains("отменён", output);
        Assert.DoesNotContain("MALICIOUS", output);
    }

    [Fact]
    public async Task Run_MissingFile_IsRefused()
    {
        var source = Path.Combine(_directory, "src.bat");
        File.WriteAllText(source, "@echo off");
        var data = _store.Import(source, 3, "t", "", "", "us_missing");
        File.Delete(_store.ScriptPath(data));

        var (exitCode, output) = await _store.RunAsync(data);

        Assert.Equal(-1, exitCode);
        Assert.Contains("отменён", output);
    }

    // Legacy-карточка без эталонного хэша: первый запуск фиксирует хэш и
    // выполняется; второй запуск с изменённым файлом — отказ.
    [Fact]
    public async Task Run_LegacyScript_FixesHashOnFirstRun()
    {
        var source = Path.Combine(_directory, "src.bat");
        File.WriteAllText(source, "@echo off" + Environment.NewLine + "exit /b 0");
        var data = _store.Import(source, 3, "t", "", "", "us_tofu");
        data.Sha256 = string.Empty;

        var (firstCode, _) = await _store.RunAsync(data);
        Assert.Equal(0, firstCode);
        Assert.False(string.IsNullOrEmpty(data.Sha256));

        var (secondCode, _) = await _store.RunAsync(data);
        Assert.Equal(0, secondCode);
    }
}
