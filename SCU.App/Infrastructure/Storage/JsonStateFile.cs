using System.Text.Json;
using SCU.Common;

namespace SCU.Infrastructure.Storage;

// Каталог состояния приложения: %AppData%\SCU\state (рядом с settings.json).
internal static class DashboardStatePaths
{
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU",
        "state");
}

// Общий JSON-файл состояния Dashboard: список записей, ограничение ёмкости
// (старые вытесняются), безопасная запись (temp + move) и восстановление после
// повреждения: файл переименовывается в *.corrupt-<timestamp>, история стартует пустой.
// Потокобезопасность: семафор на ФАЙЛ (общий для всех экземпляров с тем же путём —
// раньше был на экземпляр, и два хранилища одного файла конкурировали); сам файл
// читается и пишется в пуле потоков (TaskRunner.RunBlocking). Ошибки записи не
// роняют вызывающий код. Файл записывается в конверте {version, items}: при
// переименовании полей старые файлы перестанут молча читаться со значениями по умолчанию.
internal sealed class JsonStateFile<T>
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Logger _logger;
    private readonly string _path;
    private readonly string _label;
    private readonly int _capacity;
    // П.24: необязательное ограничение по возрасту записей (история — 14 дней).
    private readonly Func<T, DateTime>? _timestampSelector;
    private readonly TimeSpan? _maxAge;
    private readonly SemaphoreSlim _gate;

    public JsonStateFile(Logger logger, string path, int capacity, string label)
        : this(logger, path, capacity, label, null, null)
    {
    }

    // timestampSelector + maxAge включают retention по возрасту: записи старше maxAge
    // удаляются при чтении файла (а значит и при записи, и при загрузке).
    public JsonStateFile(Logger logger, string path, int capacity, string label, Func<T, DateTime>? timestampSelector, TimeSpan? maxAge)
    {
        _logger = logger;
        _path = path;
        _capacity = capacity;
        _label = label;
        _timestampSelector = timestampSelector;
        _maxAge = maxAge;
        _gate = FileGates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
    }

    // Конверт с версией схемы. Чтение понимает и старый формат (простой массив).
    private sealed class StateEnvelope
    {
        public int Version { get; set; } = SchemaVersion;

        public List<T>? Items { get; set; }
    }

    public async Task<IReadOnlyList<T>> LoadAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await TaskRunner.RunBlocking(ReadList, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Добавляет запись и усекает список до ёмкости (старые записи вытесняются).
    public async Task AppendAsync(T item, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = await TaskRunner.RunBlocking(ReadList, ct).ConfigureAwait(false);
            items.Add(item);
            var trimmed = items.Count > _capacity
                ? items.Skip(items.Count - _capacity).ToList()
                : items;
            await TaskRunner.RunBlocking(() => WriteList(trimmed), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Полная очистка хранилища (нужно BaselineStore): файл удаляется целиком.
    // Сбой удаления не роняет вызывающий код — как и сбой записи.
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TaskRunner.RunBlocking(() =>
            {
                try
                {
                    if (File.Exists(_path))
                    {
                        File.Delete(_path);
                    }
                }
                catch (Exception exception)
                {
                    _logger.Warn($"{_label} | clear failed | {exception.Message}");
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Повреждённый файл — не ошибка приложения: содержимое теряется, но работоспособность
    // сохраняется (переименование в *.corrupt-<timestamp> сохраняет данные для разбора).
    private List<T> ReadList()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var json = File.ReadAllText(_path);
            List<T>? items;
            try
            {
                var envelope = JsonSerializer.Deserialize<StateEnvelope>(json);
                if (envelope is null)
                {
                    items = [];
                }
                else
                {
                    if (envelope.Version > SchemaVersion)
                    {
                        // Файл записан более новой версией схемы: неизвестные поля
                        // будут потеряны при перезаписи — предупреждаем.
                        _logger.Warn($"{_label} | file version {envelope.Version} > {SchemaVersion}");
                    }

                    items = envelope.Items ?? [];
                }
            }
            catch (JsonException)
            {
                // Старый формат до конвертов — простой массив.
                items = JsonSerializer.Deserialize<List<T>>(json);
            }

            return ApplyRetention(items ?? []);
        }
        catch (Exception exception)
        {
            _logger.Warn($"{_label} | state file corrupted: {exception.Message}");
            try
            {
                // Миллисекунды: два повреждения в одну секунду больше не затирают друг друга.
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", System.Globalization.CultureInfo.InvariantCulture);
                File.Move(_path, _path + ".corrupt-" + stamp);
                _logger.Warn($"{_label} | state file moved: {_path}.corrupt-{stamp}");
            }
            catch (Exception moveException)
            {
                _logger.Warn($"{_label} | state file rename failed: {moveException.Message}");
            }

            return [];
        }
    }

    // Обрезка по возрасту (П.24): записи старше maxAge удаляются при чтении —
    // тогда и Append (запись), и LoadAll (загрузка) работают только со свежими записями.
    // Формат JSON не меняется: фильтрация — часть логики хранилища.
    private List<T> ApplyRetention(List<T> items)
    {
        if (_maxAge is not { } maxAge || _timestampSelector is null || items.Count == 0)
        {
            return items;
        }

        var cutoff = DateTime.Now - maxAge;
        return items.Where(item => _timestampSelector(item) >= cutoff).ToList();
    }

    private void WriteList(List<T> items)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            var envelope = new StateEnvelope { Version = SchemaVersion, Items = items };
            File.WriteAllText(temp, JsonSerializer.Serialize(envelope, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception exception)
        {
            // П. №15 аудита: остаток .tmp после сбоя — мусор и ложный
            // «незавершённый файл» при следующем чтении.
            try { File.Delete(_path + ".tmp"); } catch { /* .tmp мог не создаться */ }
            _logger.Warn($"{_label} | save failed | {exception.Message}");
        }
    }
}
