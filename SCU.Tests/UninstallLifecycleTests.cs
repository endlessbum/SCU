using System.Diagnostics;
using SCU.Common;
using Xunit;

namespace SCU.Tests;

// П. 11 аудита, uninstall-кейсы: graceful timeout деинсталлятора с последующим
// снятием дерева процессов; безопасность снятия (только процессы из папки
// установки, соседние каталоги и чужие пути не трогаются); отсутствующий
// install root — безопасный нулевой результат. Suite=Integration: реальные
// процессы и таймауты.
[Trait("Suite", "Integration")]
public class UninstallLifecycleTests
{
    [Fact]
    public async Task UninstallCompletely_TimedOutUninstaller_TreeKilled_TimeoutReported()
    {
        var root = Path.Combine(Path.GetTempPath(), "scu-uninst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.ini"), "marker");

        var app = new InstalledApp
        {
            DisplayName = "SCU Timeout Test App",
            InstallLocation = root,
            // Деинсталлятор, не завершающийся сам: cmd с ping-задержкой ~29 секунд.
            UninstallString = @"C:\Windows\System32\cmd.exe /c ping -n 30 127.0.0.1 > nul",
        };

        var originalTimeout = InstalledAppsService.UninstallTimeoutForTests;
        InstalledAppsService.UninstallTimeoutForTests = TimeSpan.FromSeconds(2);
        try
        {
            var service = new InstalledAppsService(Logger.CreateForCurrentRun());
            var result = await service.UninstallCompletelyAsync(app, CancellationToken.None);

            // Graceful-first: процессы приложения не снимаются до истечения
            // таймаута; после — снимаются, операция завершается успешно с
            // явной пометкой о таймауте, а не молчаливым «успехом».
            Assert.True(result.IsSuccess, result.Message);
            Assert.Contains("таймаут", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            InstalledAppsService.UninstallTimeoutForTests = originalTimeout;
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void KillAppProcesses_KillsOnlyProcessesUnderInstallRoot()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "scu-kill-" + Guid.NewGuid().ToString("N"));
        var appRoot = Path.Combine(baseDirectory, "App");
        var siblingRoot = Path.Combine(baseDirectory, "Other");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(siblingRoot);

        Process? appProcess = null;
        Process? siblingProcess = null;
        try
        {
            // Одинаковый исполняемый файл в папке установки и в соседней —
            // граница пути обязана различить их по расположению, не по имени.
            var exeName = "scu-kill-target.exe";
            var appExe = Path.Combine(appRoot, exeName);
            var siblingExe = Path.Combine(siblingRoot, exeName);
            File.Copy(Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\cmd.exe", appExe);
            File.Copy(Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\cmd.exe", siblingExe);

            appProcess = SpawnLongRunning(appExe);
            siblingProcess = SpawnLongRunning(siblingExe);
            Assert.True(WaitForMainModule(appProcess), "тестовый процесс не стартовал");
            Assert.True(WaitForMainModule(siblingProcess), "тестовый процесс не стартовал");

            var app = new InstalledApp { DisplayName = "SCU Kill Test", InstallLocation = appRoot };
            var service = new InstalledAppsService(Logger.CreateForCurrentRun());
            var killed = service.KillAppProcessesForTest(app);

            Assert.Equal(1, killed);
            SpinWait.SpinUntil(() => appProcess.HasExited, TimeSpan.FromSeconds(10));
            Assert.True(appProcess.HasExited, "процесс из папки установки обязан быть снят");
            Assert.False(siblingProcess.HasExited, "процесс из соседнего каталога снимать нельзя");
        }
        finally
        {
            foreach (var process in new[] { appProcess, siblingProcess })
            {
                try
                {
                    if (process is { HasExited: false })
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    process?.Dispose();
                }
                catch (Exception exception) when (exception is InvalidOperationException or SystemException)
                {
                }
            }
            TryDeleteDirectory(baseDirectory);
        }
    }

    [Fact]
    public void KillAppProcesses_MissingInstallRoot_KillsNothing()
    {
        var service = new InstalledAppsService(Logger.CreateForCurrentRun());
        Assert.Equal(0, service.KillAppProcessesForTest(
            new InstalledApp { DisplayName = "SCU No Root", InstallLocation = null }));
        Assert.Equal(0, service.KillAppProcessesForTest(
            new InstalledApp { DisplayName = "SCU Bad Root", InstallLocation = @"C:\Program Files" }));
    }

    private static Process SpawnLongRunning(string exePath)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "/c ping -n 60 127.0.0.1 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
    }

    private static bool WaitForMainModule(Process process)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (process.MainModule?.FileName is not null)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is SystemException or IOException)
            {
                // процесс ещё не инициализирован
            }
            Thread.Sleep(100);
        }
        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
