using System.Security.Cryptography;
using SCU.Common;
using SCU.Interop;
using Xunit;

namespace SCU.Tests;

// VerifyDefinition (п. 19 аудита): имя файла, ProductName, SHA-256 allowlist — fail-closed.
public sealed class RuntimeInstallerVerifyTests : IDisposable
{
    private readonly string _directory;
    private readonly RuntimeInstaller _installer;

    public RuntimeInstallerVerifyTests()
    {
        _directory = Path.Combine(AppContext.BaseDirectory, "testdata", "rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var logger = Logger.CreateForCurrentRun();
        _installer = new RuntimeInstaller(logger, new SCURunner(logger));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    private string CreateInstallerCopy(string expectedFileName, string? sourcePath = null)
    {
        // Скачанный артефакт именуется как «base_guid.ext».
        var baseName = Path.GetFileNameWithoutExtension(expectedFileName);
        var ext = Path.GetExtension(expectedFileName);
        var path = Path.Combine(_directory, baseName + "_" + Guid.NewGuid().ToString("N") + ext);
        if (sourcePath is not null)
        {
            File.Copy(sourcePath, path);
        }
        else
        {
            File.WriteAllText(path, "not a real installer");
        }

        return path;
    }

    [Fact]
    public void VerifyDefinition_WrongFileName_Fails()
    {
        var path = Path.Combine(_directory, "wrong_name.exe");
        File.WriteAllText(path, "x");

        var result = _installer.VerifyDefinitionForTest(path, "dxwebsetup.exe");

        Assert.False(result.IsSuccess);
        Assert.Contains("не соответствует ожидаемому", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyDefinition_MatchingName_WithoutProductOrHash_Succeeds()
    {
        var path = CreateInstallerCopy("dxwebsetup.exe");

        var result = _installer.VerifyDefinitionForTest(path, "dxwebsetup.exe");

        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public void VerifyDefinition_Sha256Allowlist_RejectsUnknownHash()
    {
        var path = CreateInstallerCopy("vc_redist.x64.exe");
        var result = _installer.VerifyDefinitionForTest(
            path,
            "vc_redist.x64.exe",
            sha256Allowlist: ["DEADBEEF"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("allowlist", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyDefinition_Sha256Allowlist_AcceptsMatchingHash()
    {
        var path = CreateInstallerCopy("vc_redist.x64.exe");
        using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream));

        var result = _installer.VerifyDefinitionForTest(
            path,
            "vc_redist.x64.exe",
            sha256Allowlist: [sha256]);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public void VerifyDefinition_ProductNameMismatch_Fails()
    {
        // Текстовый файл без ProductName — Contains("Visual C++") fail-closed.
        var path = CreateInstallerCopy("vc_redist.x64.exe");

        var result = _installer.VerifyDefinitionForTest(
            path,
            "vc_redist.x64.exe",
            expectedProductNameContains: "Visual C++");

        Assert.False(result.IsSuccess);
        Assert.Contains("Продукт установщика", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyDefinition_ProductNameMatch_OnSystemBinary_Succeeds()
    {
        var source = Path.Combine(Environment.SystemDirectory, "wermgr.exe");
        if (!File.Exists(source))
        {
            const string message = "SKIPPED: wermgr.exe не найден.";
            if (Environment.GetEnvironmentVariable("SCU_ALLOW_INTEGRATION_SKIP") == "1")
            {
                Console.WriteLine(message);
                return;
            }

            Assert.Fail(message);
        }

        var path = CreateInstallerCopy("wermgr.exe", source);
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
        var fragment = (info.ProductName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Windows";

        var result = _installer.VerifyDefinitionForTest(
            path,
            "wermgr.exe",
            expectedProductNameContains: fragment);

        Assert.True(result.IsSuccess, result.Message + " | ProductName=" + info.ProductName);
    }
}
