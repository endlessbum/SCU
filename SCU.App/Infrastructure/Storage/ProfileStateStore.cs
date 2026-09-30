using SCU.Common;
using SCU.Models;

namespace SCU.Infrastructure.Storage;

// Хранилище состояния профилей: %AppData%\SCU\state\profile.json.
// Ёмкость 1 (как у BaselineStore): AppendAsync вытесняет предыдущее состояние,
// отдельной перезаписи не нужно. Та же стратегия восстановления после повреждения,
// что у остальных хранилищ Dashboard (*.corrupt-<timestamp>, старт с пустого).
public sealed class ProfileStateStore
{
    private const int Capacity = 1;

    private readonly JsonStateFile<CustomProfileState> _file;

    public ProfileStateStore(Logger logger, string? filePath = null)
    {
        _file = new JsonStateFile<CustomProfileState>(
            logger,
            filePath ?? DefaultPath(),
            Capacity,
            "PROFILE");
    }

    public static string DefaultPath() => Path.Combine(DashboardStatePaths.Directory, "profile.json");

    // Сохраняет состояние (активный профиль + словарь custom-профиля).
    public Task SaveAsync(CustomProfileState state, CancellationToken ct = default) =>
        _file.AppendAsync(state, ct);

    // Повреждённый/отсутствующий файл — не ошибка: возвращается состояние по умолчанию
    // («не следить», пустой custom-словарь), работоспособность сохраняется.
    public async Task<CustomProfileState> LoadAsync(CancellationToken ct = default)
    {
        var all = await _file.LoadAllAsync(ct).ConfigureAwait(false);
        return all.Count == 0 ? new CustomProfileState() : all[^1];
    }
}
