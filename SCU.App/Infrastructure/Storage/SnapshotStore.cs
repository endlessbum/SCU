using SCU.Common;
using SCU.Models;

namespace SCU.Infrastructure.Storage;

// Хранилище снимков состояния ПК: %AppData%\SCU\state\snapshots.json.
// Append с ограничением 50 записей; повреждённый файл отдаёт пустой список
// и уходит в *.corrupt-<timestamp> (см. JsonStateFile).
public sealed class SnapshotStore
{
    private const int Capacity = 50;

    private readonly JsonStateFile<SystemSnapshot> _file;

    public SnapshotStore(Logger logger, string? filePath = null)
    {
        _file = new JsonStateFile<SystemSnapshot>(
            logger,
            filePath ?? DefaultPath(),
            Capacity,
            "SNAPSHOT");
    }

    public static string DefaultPath() => Path.Combine(DashboardStatePaths.Directory, "snapshots.json");

    public Task SaveAsync(SystemSnapshot snapshot, CancellationToken ct = default) =>
        _file.AppendAsync(snapshot, ct);

    public Task<IReadOnlyList<SystemSnapshot>> LoadAllAsync(CancellationToken ct = default) =>
        _file.LoadAllAsync(ct);

    public async Task<SystemSnapshot?> LoadLatestAsync(CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct).ConfigureAwait(false);
        return all.Count == 0
            ? null
            : all.OrderBy(snapshot => snapshot.Timestamp).Last();
    }
}
