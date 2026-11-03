using Microsoft.Win32;
using SCU.Common;
using Xunit;

namespace SCU.Tests;

// Apply/Restore/Reset: backup → change → verify → rollback на сбое.
// Только HKCU под уникальным ключом — без прав администратора и без HKLM.
public sealed class RegistryHelperApplyTests : IDisposable
{
    private readonly string _subKey;
    private readonly string _backupDir;
    private readonly RegistryHelper _registry;
    private readonly Logger _logger;

    public RegistryHelperApplyTests()
    {
        var id = Guid.NewGuid().ToString("N");
        // Не Software\SCU: боевой SCU может ужесточать ACL на своём дереве.
        _subKey = @"Software\SCUTests\RegApply\" + id;
        // Не %TEMP%: часть агентских/тестовых хостов режет запись в Temp.
        _backupDir = Path.Combine(AppContext.BaseDirectory, "testdata", "reg-" + id);
        Directory.CreateDirectory(_backupDir);
        _logger = Logger.CreateForCurrentRun();
        _registry = new RegistryHelper(_logger);
    }

    public void Dispose()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SCUTests\RegApply", writable: true);
            var leaf = _subKey.Split('\\')[^1];
            key?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
        }
        catch
        {
            // Временный ключ не критичен.
        }

        try
        {
            if (Directory.Exists(_backupDir))
            {
                Directory.Delete(_backupDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private RegistryTweak Tweak(string name, object off, object? restore = null) =>
        new(RegistryHive.CurrentUser, _subKey, name, RegistryValueKind.DWord, off, restore);

    private string BackupFile(string name) => Path.Combine(_backupDir, name);

    [SkippableFact]
    public void Apply_WritesOffValue_CreatesBackup_AndIsApplied()
    {
        Skip.IfNot(TestEnvironment.RegistryWritable,
            "HKCU-запись недоступна из хоста тестов (изолированная песочница) — реестровый кейс пропущен.");

        using (var key = Registry.CurrentUser.CreateSubKey(_subKey, true))
        {
            key!.SetValue("Flag", 0, RegistryValueKind.DWord);
        }

        var tweaks = new[] { Tweak("Flag", 1, 0) };
        var backup = BackupFile("flag.json");

        var result = _registry.Apply(tweaks, backup);

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(File.Exists(backup));
        Assert.True(_registry.IsApplied(tweaks));

        using var read = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Equal(1, read!.GetValue("Flag"));
    }

    [SkippableFact]
    public void Apply_DoesNotOverwriteExistingBackup()
    {
        Skip.IfNot(TestEnvironment.RegistryWritable,
            "HKCU-запись недоступна из хоста тестов (изолированная песочница) — реестровый кейс пропущен.");

        using (var key = Registry.CurrentUser.CreateSubKey(_subKey, true))
        {
            key!.SetValue("Flag", 7, RegistryValueKind.DWord);
        }

        var tweaks = new[] { Tweak("Flag", 1, 0) };
        var backup = BackupFile("keep.json");
        Assert.True(_registry.Apply(tweaks, backup).IsSuccess);

        using (var key = Registry.CurrentUser.OpenSubKey(_subKey, true))
        {
            key!.SetValue("Flag", 99, RegistryValueKind.DWord);
        }

        // Повторный Apply с тем же файлом: исходный бэкап (7) не перезаписывается.
        var firstBackup = File.ReadAllText(backup);
        Assert.True(_registry.Apply(tweaks, backup).IsSuccess);
        Assert.Equal(firstBackup, File.ReadAllText(backup));

        Assert.True(_registry.Restore(tweaks, backup).IsSuccess);
        using var read = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Equal(7, read!.GetValue("Flag"));
    }

    [Fact]
    public void Restore_MissingBackup_Fails()
    {
        var result = _registry.Restore(
            [Tweak("X", 1)],
            Path.Combine(_backupDir, "нет.json"));

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Code);
    }

    [SkippableFact]
    public void Restore_RoundTrip_ReturnsOriginalValue()
    {
        Skip.IfNot(TestEnvironment.RegistryWritable,
            "HKCU-запись недоступна из хоста тестов (изолированная песочница) — реестровый кейс пропущен.");

        using (var key = Registry.CurrentUser.CreateSubKey(_subKey, true))
        {
            key!.SetValue("A", 3, RegistryValueKind.DWord);
        }

        var tweaks = new[] { Tweak("A", 9, 3) };
        var backup = BackupFile("roundtrip.json");
        Assert.True(_registry.Apply(tweaks, backup).IsSuccess);
        Assert.True(_registry.Restore(tweaks, backup).IsSuccess);

        using var read = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Equal(3, read!.GetValue("A"));
    }

    [SkippableFact]
    public void Restore_SkipsUnknownSnapshotValues_WithCode2()
    {
        Skip.IfNot(TestEnvironment.RegistryWritable,
            "HKCU-запись недоступна из хоста тестов (изолированная песочница) — реестровый кейс пропущен.");

        using (var key = Registry.CurrentUser.CreateSubKey(_subKey, true))
        {
            key!.SetValue("Known", 0, RegistryValueKind.DWord);
            key.SetValue("Orphan", 5, RegistryValueKind.DWord);
        }

        var allTweaks = new[]
        {
            Tweak("Known", 1, 0),
            Tweak("Orphan", 1, 0)
        };
        var backup = BackupFile("orphan.json");
        Assert.True(_registry.Apply(allTweaks, backup).IsSuccess);

        // Restore только по Known — Orphan из бэкапа пропускается (код 2).
        var result = _registry.Restore([Tweak("Known", 1, 0)], backup);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Code);
        Assert.Contains("Пропущены", result.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void ResetToDefault_WritesRestoreValue_OrDeletesWhenNull()
    {
        Skip.IfNot(TestEnvironment.RegistryWritable,
            "HKCU-запись недоступна из хоста тестов (изолированная песочница) — реестровый кейс пропущен.");

        using (var key = Registry.CurrentUser.CreateSubKey(_subKey, true))
        {
            key!.SetValue("Keep", 0, RegistryValueKind.DWord);
            key.SetValue("Drop", 1, RegistryValueKind.DWord);
        }

        var tweaks = new[]
        {
            Tweak("Keep", 0, restore: 1),
            Tweak("Drop", 1, restore: null)
        };

        Assert.True(_registry.ResetToDefault(tweaks).IsSuccess);

        using var read = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Equal(1, read!.GetValue("Keep"));
        Assert.Null(read.GetValue("Drop"));
    }

    [SkippableFact]
    public void Apply_MissingValue_CreatesAndIsApplied()
    {
        // Параметра ещё нет — Exists=false в бэкапе, OffValue записывается.
        Skip.IfNot(TestEnvironment.RegistryWritable,
            "HKCU-запись недоступна из хоста тестов (изолированная песочница) — реестровый кейс пропущен.");

        Registry.CurrentUser.CreateSubKey(_subKey)?.Dispose();

        var tweaks = new[] { Tweak("NewFlag", 42) };
        var backup = BackupFile("new.json");

        Assert.True(_registry.Apply(tweaks, backup).IsSuccess);
        Assert.True(_registry.IsApplied(tweaks));

        Assert.True(_registry.Restore(tweaks, backup).IsSuccess);
        using var read = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Null(read!.GetValue("NewFlag"));
    }
}
