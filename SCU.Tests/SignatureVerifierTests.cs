using SCU.Common;
using Xunit;

namespace SCU.Tests;

public class SignatureVerifierTests
{
    [Fact]
    public void MicrosoftSignedSystemBinary_PassesVerification()
    {
        // wermgr.exe имеет читаемую .NET сертификатную таблицу; у части системных
        // бинарников (cmd.exe, notepad.exe) new X509Certificate2 бросает CRYPT_E_NOT_FOUND.
        var target = Path.Combine(Environment.SystemDirectory, "wermgr.exe");
        if (!File.Exists(target))
        {
            return;
        }

        var result = SignatureVerifier.VerifyMicrosoftSigned(target, minBytes: 1024);

        Assert.True(result.IsSuccess, "wermgr.exe должен проходить проверку: " + result.Message);
    }

    [Fact]
    public void UnsignedFile_Fails()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "scu_sigtest_" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(tempFile, "not a signed binary, just text");

            var result = SignatureVerifier.VerifyMicrosoftSigned(tempFile, minBytes: 4);

            Assert.False(result.IsSuccess);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void MissingFile_FailsWithCode2()
    {
        var result = SignatureVerifier.VerifyMicrosoftSigned(@"Z:\несуществующий\файл.exe");

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Code);
    }

    [Fact]
    public void TinyFile_FailsSizeCheck()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "scu_sigtest_" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(tempFile, "tiny");

            var result = SignatureVerifier.VerifyMicrosoftSigned(tempFile, minBytes: 262144);

            Assert.False(result.IsSuccess);
            Assert.Equal(3, result.Code);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
