using System.Diagnostics;
using SCU.Common;
using Xunit;

namespace SCU.Tests;

// SCU-02: VerifiedLaunch обязан открывать блокирующий дескриптор ДО проверки
// подписи, чтобы окно «подпись проверена — дескриптор ещё не получен» нельзя
// было использовать для подмены файла. Все проверки — на временных копиях,
// системные файлы не изменяются.
public class VerifiedLaunchTests : IDisposable
{
    private readonly string _root = TestScratch.NewDir("scu-verified-launch-");
    private string? _junctionPath;

    public void Dispose()
    {
        // Junction удаляется первым и только как точку перемонтирования:
        // рекурсивное удаление дерева через junction падает с IOException.
        if (_junctionPath is not null && Directory.Exists(_junctionPath))
        {
            Directory.Delete(_junctionPath, recursive: false);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void OpenLocked_MissingFile_ReturnsNull()
    {
        Assert.Null(VerifiedLaunch.OpenLocked(
            Path.Combine(_root, "missing.exe"), Logger.CurrentRun));
    }

    [Fact]
    public void OpenLocked_UnderSizedFile_ReturnsNull()
    {
        // Ниже minBytes (262144): подпись даже не проверяется, но дескриптор
        // всё равно не выдаётся и запуск запрещён.
        var path = Path.Combine(_root, "tiny.exe");
        File.WriteAllBytes(path, new byte[1024]);

        Assert.Null(VerifiedLaunch.OpenLocked(path, Logger.CurrentRun));
    }

    [Fact]
    public void OpenLocked_ReparsePoint_ReturnsNull()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target"));
        var junctionPath = Path.Combine(_root, "junction");
        var mklink = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{target.FullName}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        mklink!.WaitForExit();

        Skip.IfNot(Directory.Exists(junctionPath), "mklink /J недоступен в этой среде.");
        _junctionPath = junctionPath;

        Assert.Null(VerifiedLaunch.OpenLocked(junctionPath, Logger.CurrentRun));
    }

    [Fact]
    public void OpenLocked_SignedFile_LockBlocksDeletionUntilReleased()
    {
        const string signedSource = @"C:\Windows\explorer.exe";
        Skip.IfNot(File.Exists(signedSource), "explorer.exe не найден в этой среде.");

        // Копия сохраняет встроенную Authenticode-подпись — проверка проходит
        // так же, как для оригинала.
        var copy = Path.Combine(_root, "signed-copy.exe");
        File.Copy(signedSource, copy);

        using var lockStream = VerifiedLaunch.OpenLocked(copy, Logger.CurrentRun);
        Assert.NotNull(lockStream);

        // Пока дескриптор жив, удаление/замена другим процессом невозможны.
        Assert.ThrowsAny<IOException>(() => File.Delete(copy));

        lockStream.Dispose();
        File.Delete(copy);
        Assert.False(File.Exists(copy));
    }
}
