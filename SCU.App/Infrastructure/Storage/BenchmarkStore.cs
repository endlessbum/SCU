using SCU.Common;
using SCU.Models.Benchmark;

namespace SCU.Infrastructure.Storage;

// Журнал запусков бэнчмарка: %AppData%\SCU\state\benchmark.json.
// Хранит до 20 запусков (новейшие в конце): последняя запись — текущий индекс,
// предпоследняя — точка сравнения «до», первая — исходное состояние.
// Тот же механизм безопасной записи и восстановления после повреждения,
// что у SnapshotStore/HistoryStore/BaselineStore.
public sealed class BenchmarkStore
{
    private const int Capacity = 20;

    private readonly JsonStateFile<BenchmarkRun> _file;

    public BenchmarkStore(Logger logger, string? filePath = null)
    {
        _file = new JsonStateFile<BenchmarkRun>(
            logger,
            filePath ?? DefaultPath(),
            Capacity,
            "BENCHMARK");
    }

    public static string DefaultPath() => Path.Combine(DashboardStatePaths.Directory, "benchmark.json");

    // Добавляет запуск (старые вытесняются ёмкостью).
    public Task AppendAsync(BenchmarkRun run, CancellationToken ct = default) =>
        _file.AppendAsync(run, ct);

    public async Task<IReadOnlyList<BenchmarkRun>> LoadAllAsync(CancellationToken ct = default) =>
        await _file.LoadAllAsync(ct).ConfigureAwait(false);

    public Task ClearAsync(CancellationToken ct = default) => _file.ClearAsync(ct);
}
