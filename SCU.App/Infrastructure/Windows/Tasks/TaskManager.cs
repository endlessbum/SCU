using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.Infrastructure.Windows.Tasks;

public sealed class TaskManager
{
    private readonly SCURunner _runner;

    public TaskManager(SCURunner runner)
    {
        _runner = runner;
    }

    public static IReadOnlyList<string> DefaultTaskPaths { get; } =
    [
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Application Experience\StartupAppTask",
        @"\Microsoft\Windows\Autochk\Proxy",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
        @"\Microsoft\Windows\Feedback\Siuf\DmClient",
        @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
        @"\Microsoft\Windows\Maps\MapsToastTask",
        @"\Microsoft\Windows\Maps\MapsUpdateTask",
        @"\Microsoft\Windows\Shell\FamilySafetyMonitor",
        @"\Microsoft\Windows\Shell\FamilySafetyRefreshTask",
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
        @"\Microsoft\XblGameSave\XblGameSaveTask"
    ];

    public static string DefaultTasksJoined => string.Join(";", DefaultTaskPaths);

    // Отдельного TasksList в SCU.ps1 нет — состояние читаем через TasksBackup.
    public async Task<Result<IReadOnlyList<ScheduledTaskInfo>>> ListAsync(
        string outFile,
        CancellationToken ct = default)
    {
        if (!_runner.IsAvailable)
        {
            return Result<IReadOnlyList<ScheduledTaskInfo>>.Failure("SCU.ps1 не найден.", 2);
        }

        try
        {
            var directory = Path.GetDirectoryName(outFile);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var result = await _runner.RunAsync(
                "TasksBackup",
                new Dictionary<string, string?>
                {
                    ["OutFile"] = outFile,
                    ["Tasks"] = DefaultTasksJoined
                },
                progress: null,
                ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return Result<IReadOnlyList<ScheduledTaskInfo>>.Failure(result.Message, result.Code);
            }

            var items = ParseListFile(outFile);
            return Result<IReadOnlyList<ScheduledTaskInfo>>.Success(items);
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyList<ScheduledTaskInfo>>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<ScheduledTaskInfo>>.Failure(exception.Message);
        }
    }

    public static IReadOnlyList<ScheduledTaskInfo> ParseListFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var items = new List<ScheduledTaskInfo>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('\t');
            var full = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(full))
            {
                continue;
            }

            var state = parts.Length >= 2 ? parts[1].Trim() : string.Empty;
            items.Add(new ScheduledTaskInfo(full, state));
        }

        return items;
    }
}
