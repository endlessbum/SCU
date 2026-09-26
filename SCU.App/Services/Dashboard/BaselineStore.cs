using SCU.Common;
using SCU.Models;

namespace SCU.Services.Dashboard;

// Хранилище baseline (эталонный снимок «как было»): %AppData%\SCU\state\baseline.json.
// Ёмкость 1: AppendAsync вытесняет предыдущий baseline, отдельной перезаписи не нужно.
// Та же стратегия восстановления после повреждения, что у SnapshotStore/HistoryStore.
public sealed class BaselineStore
{
    private const int Capacity = 1;

    private readonly JsonStateFile<SystemSnapshot> _file;

    public BaselineStore(Logger logger, string? filePath = null)
    {
        _file = new JsonStateFile<SystemSnapshot>(
            logger,
            filePath ?? DefaultPath(),
            Capacity,
            "BASELINE");
    }

    public static string DefaultPath() => Path.Combine(DashboardStatePaths.Directory, "baseline.json");

    // Сохраняет снимок как новый baseline (предыдущий вытесняется ёмкостью 1).
    public Task SaveAsync(SystemSnapshot snapshot, CancellationToken ct = default) =>
        _file.AppendAsync(snapshot, ct);

    public async Task<SystemSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        var all = await _file.LoadAllAsync(ct).ConfigureAwait(false);
        return all.Count == 0
            ? null
            : all.OrderBy(snapshot => snapshot.Timestamp).Last();
    }

    public Task ClearAsync(CancellationToken ct = default) => _file.ClearAsync(ct);
}
