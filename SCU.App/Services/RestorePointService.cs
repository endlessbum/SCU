using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using SCU.Common;
using SCU.Services.Dashboard;

namespace SCU.Services;

// Точки восстановления системной защиты (System Restore).
// Создание — через WMI-класс root\default:SystemRestore (как Checkpoint-Computer),
// чтение — запросом по тому же классу. Лимит «одна точка в 24 часа» обходится
// временной записью SystemRestorePointCreationFrequency=0 с возвратом исходного значения.
// ЛИМИТ ХРАНЕНИЯ: в системном хранилище держится не более MaxRestorePoints (5) точек.
// После каждого успешного создания список подрезается: самая старая точка (любая,
// включая созданные системой или другим ПО) удаляется через srclient!SRRemoveRestorePoint.
// Созданные приложением точки дополнительно tracked в собственном store
// (%AppData%\SCU\state\restore_points.json) — учёт идентификаторов. ОГРАНИЧЕНИЕ:
// SRRemoveRestorePoint требует прав администратора и может не поддерживаться на части
// систем — если API вернул ошибку, точка остаётся в хранилище (лимит доберётся при
// следующем создании), об этом пишется предупреждение в журнал.
public sealed class RestorePointService
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValueName = "SystemRestorePointCreationFrequency";
    private const int ModifySettingsRestorePointType = 12;

    private readonly Logger _logger;

    public RestorePointService(Logger logger)
    {
        _logger = logger;
    }

    // Удаление точки восстановления по SequenceNumber: srclient.dll (WinAPI).
    [DllImport("srclient.dll", SetLastError = true)]
    private static extern int SRRemoveRestorePoint(uint dwSequenceNumber, uint dwRestorePointType);

    // DisableSR: 1 = защита системы отключена; значение отсутствует = включена по умолчанию.
    // Глобальное DisableSR=0 не гарантирует защиту системного тома: защита может быть
    // снята выборочно (per-volume DisableSR в подключе с GUID тома).
    public static bool IsSystemProtectionEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKey);
            var globalDisabled = key?.GetValue("DisableSR") switch
            {
                int value => value != 0,
                null => false,
                _ => false
            };
            if (globalDisabled)
            {
                return false;
            }

            return !IsSystemVolumeProtectionDisabled(key);
        }
        catch
        {
            return true;
        }
    }

    // Подключи SystemRestore именуются путями вида \\?\Volume{guid};
    // ищем подключ системного тома. GUID или подключ не нашлись — остаётся
    // результат глобальной проверки (не выдумываем «защита отключена»).
    private static bool IsSystemVolumeProtectionDisabled(RegistryKey? key)
    {
        try
        {
            if (key is null || key.SubKeyCount == 0)
            {
                return false;
            }

            var volumeGuid = GetSystemVolumeGuid();
            if (volumeGuid is null)
            {
                return false;
            }

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                if (!subKeyName.Contains(volumeGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var volumeKey = key.OpenSubKey(subKeyName);
                return volumeKey?.GetValue("DisableSR") is int perVolume && perVolume != 0;
            }
        }
        catch
        {
            // Ошибка чтения per-volume состояния не должна инвертировать ответ.
        }

        return false;
    }

    private static string? GetSystemVolumeGuid()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrEmpty(systemRoot) || systemRoot.Length < 3)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(260);
        if (!GetVolumeNameForVolumeMountPoint(systemRoot[..3], builder, (uint)builder.Capacity))
        {
            return null;
        }

        // Оставляем часть "{guid}": формат подключения (1 или 2 слэша) не важен.
        var volume = builder.ToString();
        var brace = volume.IndexOf('{');
        return brace >= 0 ? volume[brace..] : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string mountPoint, System.Text.StringBuilder volumeName, uint bufferLength);

    // Создание точки без идентификатора (используется там, где id не нужен).
    // Общая логика — в CreateCheckpointCoreAsync; дублирования с WithId-версией нет.
    public Task<Result> CreateCheckpointAsync(string description, CancellationToken ct = default) =>
        CreateCheckpointCoreAsync(description, ct);

    // Создание точки с надёжным идентификатором: до создания читается список
    // SequenceNumber, после — повторно; новой считается та, которой не было в списке
    // до создания (см. FindNewSequenceNumber). Value = "RP-<seq>"; null = точка
    // создана, но id определить не удалось (WMI-списки не читались или новой точки
    // не видно) — выдумывать идентификатор нельзя.
    public async Task<Result<string?>> CreateCheckpointWithIdAsync(string description, CancellationToken ct = default)
    {
        var beforeKnown = TryQuerySequenceNumbers(out var before);
        var core = await CreateCheckpointCoreAsync(description, ct).ConfigureAwait(false);
        if (!core.IsSuccess)
        {
            return Result<string?>.Failure(core.Message, core.Code);
        }

        string? id = null;
        if (beforeKnown && TryQuerySequenceNumbers(out var after)
            && FindNewSequenceNumber(before, after) is { } sequence)
        {
            id = "RP-" + sequence.ToString(CultureInfo.InvariantCulture);

            // П.15в: надёжный идентификатор регистрируется в store приложения с обрезкой
            // до лимита. Здесь sequence — уже int, повторного разбора не требуется.
            TrackCreatedRestorePoint(sequence, description);
        }

        _logger.Info("RP | create id | " + (id ?? "(не определён)"));
        return Result<string?>.Success(id);
    }

    // Новый идентификатор = SequenceNumber из after, которого не было в before.
    // Если новых несколько (теоретически), берётся наибольший — у точек он монотонно
    // растёт, значит это созданная последней. Чистая функция — покрыта юнит-тестами.
    public static int? FindNewSequenceNumber(IReadOnlyCollection<int> before, IReadOnlyCollection<int> after)
    {
        var known = new HashSet<int>(before);
        int? newest = null;
        foreach (var sequence in after)
        {
            if (!known.Contains(sequence) && (newest is null || sequence > newest.Value))
            {
                newest = sequence;
            }
        }

        return newest;
    }

    private Task<Result> CreateCheckpointCoreAsync(string description, CancellationToken ct) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();

        if (!IsSystemProtectionEnabled())
        {
            return Result.Failure(
                "Защита системы отключена. Включите её в «Параметрах → О системе → Защита системы» или средствами этого раздела.");
        }

        try
        {
            using var frequencyKey = Registry.LocalMachine.OpenSubKey(SystemRestoreKey, writable: true);
            if (frequencyKey is null)
            {
                // Без прав обход лимита недоступен: WMI вернёт ошибку, если точка за
                // последние 24 часа уже создавалась. Предупреждаем заранее, чтобы не
                // разбирать невнятный код WMI постфактум.
                _logger.Warn("RP | create | нет прав на " + FrequencyValueName + " — обход 24-часового лимита не сработает");
            }
            var originalFrequency = frequencyKey?.GetValue(FrequencyValueName);
            var restoreFrequency = false;
            try
            {
                // Лимит «не чаще одной точки в 24 часа» читается из этой величины (в минутах);
                // на время создания ставим 0, потом возвращаем как было.
                if (frequencyKey is not null && (originalFrequency is null or int and > 0))
                {
                    frequencyKey.SetValue(FrequencyValueName, 0, RegistryValueKind.DWord);
                    restoreFrequency = true;
                }

                using var restoreClass = new ManagementClass("root\\default", "SystemRestore", null);
                var parameters = restoreClass.GetMethodParameters("CreateRestorePoint");
                parameters["Description"] = description;
                parameters["RestorePointType"] = ModifySettingsRestorePointType;
                parameters["EventType"] = 100; // BEGIN_SYSTEM_CHANGE, как у Checkpoint-Computer
                // InvokeMethod синхронный и неотменяемый; таймаут защищает от вечного
                // зависания на сломанном WMI (создание обычно занимает секунды).
                var output = restoreClass.InvokeMethod(
                    "CreateRestorePoint",
                    parameters,
                    new InvokeMethodOptions { Timeout = TimeSpan.FromMinutes(5) });
                // Проверка отмены ПОСЛЕ вызова убрана: InvokeMethod синхронный и неотменяемый,
                // точка к этому моменту уже создана — «Отменено» здесь было ложью.
                if (output is null || output["ReturnValue"] is null)
                {
                    _logger.Error("RP | create | WMI returned no ReturnValue");
                    return Result.Failure("WMI SystemRestore не вернул код результата.");
                }

                var result = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
                if (result != 0)
                {
                    _logger.Error("RP | create | WMI rc=" + result);
                    return Result.Failure($"WMI SystemRestore вернул код {result}.");
                }

                _logger.Info("RP | create ok | " + description);

                // Лимит хранения: подрезаем системный список до MaxRestorePoints —
                // шестая по счёту точка перезаписывает самую старую.
                await TrimSystemRestorePointsToLimitAsync().ConfigureAwait(false);
                return Result.Success("Точка восстановления создана.");
            }
            finally
            {
                if (restoreFrequency && frequencyKey is not null)
                {
                    if (originalFrequency is not null)
                    {
                        frequencyKey.SetValue(FrequencyValueName, originalFrequency, RegistryValueKind.DWord);
                    }
                    else
                    {
                        try
                        {
                            frequencyKey.DeleteValue(FrequencyValueName, throwOnMissingValue: false);
                        }
                        catch
                        {
                        }
                    }
                }

                frequencyKey?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            _logger.Error("RP | create failed | " + exception.Message);
            return Result.Failure("Не удалось создать точку восстановления: " + exception.Message);
        }
    }, CancellationToken.None);

    // Список существующих точек: структурные записи (SequenceNumber, дата, тип,
    // описание) — раздел «Поиск и целостность» показывает их списком и выполняет
    // восстановление по выбранной. Класс отсутствует или WMI недоступен — считаем,
    // что точек нет (не ошибка раздела).
    public Task<Result<IReadOnlyList<RestorePointInfo>>> GetRestorePointsAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\default",
                "SELECT SequenceNumber, Description, CreationTime, RestorePointType FROM SystemRestore");
            var points = new List<RestorePointInfo>();
            using var results = searcher.Get();
            foreach (var point in results.OfType<ManagementObject>())
            {
                using (point)
                {
                    var created = point["CreationTime"] is string wmiTime
                        ? ManagementDateTimeConverter.ToDateTime(wmiTime)
                        : DateTime.MinValue;
                    var sequence = Convert.ToInt32(point["SequenceNumber"], CultureInfo.InvariantCulture);
                    var type = point["RestorePointType"] switch
                    {
                        0 => "установка приложения",
                        1 => "удаление приложения",
                        10 => "установка драйвера",
                        12 => "изменение настроек",
                        13 => "отменённая операция",
                        _ => "тип " + point["RestorePointType"]
                    };
                    points.Add(new RestorePointInfo(
                        sequence,
                        created,
                        type,
                        point["Description"] as string ?? string.Empty));
                }
            }

            // Порядок — по убыванию даты (новейшие сверху), как в прежнем текстовом списке.
            points.Sort((a, b) => b.CreationTime.CompareTo(a.CreationTime));
            return Result<IReadOnlyList<RestorePointInfo>>.Success(points);
        }
        catch (OperationCanceledException)
        {
            // Pre-cancelled токен (проверка внутри делегата) — честная отмена, а не «список недоступен».
            return Result<IReadOnlyList<RestorePointInfo>>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            // Маскировать ошибку под «точек нет» нельзя: пользователь решит, что
            // точки удалены. Честная неудача — раздел покажет проблему.
            _logger.Warn("RP | list failed | " + exception.Message);
            return Result<IReadOnlyList<RestorePointInfo>>.Failure(
                "Не удалось прочитать список точек восстановления: " + exception.Message);
        }
    }, CancellationToken.None);

    // Запуск отката Windows SystemRestore к выбранной точке (root\default:SystemRestore.Restore).
    // ReturnValue 0 = восстановление инициировано: система уйдёт в перезагрузку сама.
    // Метод синхронный и неотменяемый — токен проверяется до вызова.
    public Task<Result> RestoreAsync(int sequenceNumber, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var restoreClass = new ManagementClass("root\\default", "SystemRestore", null);
            var parameters = restoreClass.GetMethodParameters("Restore");
            parameters["SequenceNumber"] = (uint)sequenceNumber;
            var output = restoreClass.InvokeMethod("Restore", parameters, null);
            if (output is null || output["ReturnValue"] is null)
            {
                _logger.Error("RP | restore | WMI returned no ReturnValue");
                return Result.Failure("WMI SystemRestore не вернул код результата.");
            }

            var code = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
            if (code != 0)
            {
                _logger.Error("RP | restore | WMI rc=" + code);
                return Result.Failure($"WMI SystemRestore.Restore вернул код {code}.");
            }

            _logger.Info("RP | restore started | seq=" + sequenceNumber);
            return Result.Success("Восстановление системы запущено. Компьютер будет перезагружен.");
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            _logger.Error("RP | restore failed | " + exception.Message);
            return Result.Failure("Не удалось запустить восстановление системы: " + exception.Message);
        }
    }, CancellationToken.None);

    // Удаление точки восстановления по SequenceNumber (srclient!SRRemoveRestorePoint,
    // тип 12 — как при создании). Требуются права администратора; на части систем API
    // может вернуть ошибку — она честно возвращается вызывающему.
    public Task<Result> DeleteAsync(int sequenceNumber, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var code = SRRemoveRestorePoint((uint)sequenceNumber, ModifySettingsRestorePointType);
            if (code == 0)
            {
                _logger.Info("RP | deleted | seq=" + sequenceNumber);
                return Result.Success("Точка восстановления удалена.");
            }

            _logger.Warn("RP | SRRemoveRestorePoint rc=" + code + " | seq=" + sequenceNumber);
            return code == 5
                ? Result.Failure("Нет прав на удаление точки — запустите SCU от имени администратора.", code)
                : Result.Failure($"Не удалось удалить точку восстановления (код {code}).", code);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            _logger.Error("RP | delete failed | seq=" + sequenceNumber + " | " + exception.Message);
            return Result.Failure("Не удалось удалить точку восстановления: " + exception.Message);
        }
    }, CancellationToken.None);

    // Чтение SequenceNumber всех точек. false = список недоступен (WMI-ошибка) —
    // в этом случае id новой точки сознательно не определяется (нельзя сравнивать
    // «до» и «после», если «до» неизвестно).
    private bool TryQuerySequenceNumbers(out IReadOnlyList<int> numbers)
    {
        try
        {
            var sequences = new List<int>();
            using var searcher = new ManagementObjectSearcher(
                "root\\default",
                "SELECT SequenceNumber FROM SystemRestore");
            using var results = searcher.Get();
            foreach (var point in results.OfType<ManagementObject>())
            {
                using (point)
                {
                    sequences.Add(Convert.ToInt32(point["SequenceNumber"], CultureInfo.InvariantCulture));
                }
            }

            numbers = sequences;
            return true;
        }
        catch (Exception exception)
        {
            _logger.Warn("RP | sequence list failed | " + exception.Message);
            numbers = [];
            return false;
        }
    }

    // ===================== П.15в: store созданных приложением точек =====================

    // Жёсткий лимит точек в системном хранилище.
    public const int MaxRestorePoints = 5;

    // Учёт store приложения ограничен тем же числом.
    public const int MaxTrackedRestorePoints = MaxRestorePoints;

    // Запись store: точка, созданная приложением (SequenceNumber + дата + описание).
    // JSON-файл без истории версий: System.Text.Json подставит default для отсутствующих полей.
    public sealed record AppRestorePointRecord(int SequenceNumber, DateTime CreatedAt, string Description);

    // Точка из системного хранилища (для списка раздела).
    public sealed record RestorePointInfo(int SequenceNumber, DateTime CreationTime, string Type, string Description);

    private static string DefaultTrackedStorePath() =>
        Path.Combine(DashboardStatePaths.Directory, "restore_points.json");

    // Чистая функция обрезки: остаются newest max записей, removed — выписанные старые.
    // Вход может быть в любом порядке; статичность — ради юнит-тестов.
    public static (IReadOnlyList<AppRestorePointRecord> Kept, IReadOnlyList<AppRestorePointRecord> Removed)
        TrimTrackedRestorePoints(IReadOnlyList<AppRestorePointRecord> records, int max)
    {
        if (records.Count <= max)
        {
            return (records, []);
        }

        var ordered = records.OrderByDescending(record => record.SequenceNumber).ToList();
        var kept = ordered.Take(Math.Max(0, max)).ToList();
        var removed = ordered.Skip(Math.Max(0, max)).ToList();
        return (kept, removed);
    }

    public static IReadOnlyList<AppRestorePointRecord> LoadTrackedRestorePoints(string? filePath = null)
    {
        try
        {
            var path = filePath ?? DefaultTrackedStorePath();
            if (!File.Exists(path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<AppRestorePointRecord>>(File.ReadAllText(path)) ?? [];
        }
        catch
        {
            // Store — вспомогательный функционал: сбой чтения не должен ломать раздел.
            return [];
        }
    }

    public static void SaveTrackedRestorePoints(IReadOnlyList<AppRestorePointRecord> records, string? filePath = null)
    {
        try
        {
            var path = filePath ?? DefaultTrackedStorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Как и чтение: сбой записи store не роняет операцию создания точки.
        }
    }

    // Регистрация созданной точки в store приложения (учёт идентификаторов).
    // Системное удаление лишних точек выполняет TrimSystemRestorePointsToLimitAsync —
    // она работает и с точками, созданными вне приложения.
    private void TrackCreatedRestorePoint(int sequenceNumber, string description)
    {
        var records = LoadTrackedRestorePoints().ToList();
        records.Add(new AppRestorePointRecord(sequenceNumber, DateTime.Now, description));
        var (kept, removed) = TrimTrackedRestorePoints(records, MaxTrackedRestorePoints);

        SaveTrackedRestorePoints(kept);
        _logger.Info($"RP | tracked store | kept={kept.Count} removed={removed.Count}");
    }

    // Подрезка системного хранилища до MaxRestorePoints: удаляются самые старые
    // точки (список читается по убыванию даты — хвост и есть самые старые).
    // Сбои удаления не влияют на результат создания: точка уже создана, лимит
    // доберётся при следующей попытке.
    private async Task TrimSystemRestorePointsToLimitAsync()
    {
        var points = await GetRestorePointsAsync(CancellationToken.None).ConfigureAwait(false);
        if (!points.IsSuccess || points.Value is null || points.Value.Count <= MaxRestorePoints)
        {
            return;
        }

        for (var index = points.Value.Count - 1; index >= MaxRestorePoints; index--)
        {
            var oldest = points.Value[index];
            _logger.Info("RP | trim | удаляется самая старая точка seq=" + oldest.SequenceNumber);
            TryDeleteSystemRestorePoint(new AppRestorePointRecord(
                oldest.SequenceNumber, oldest.CreationTime, oldest.Description));
        }
    }

    // Попытка удалить системную точку (srclient!SRRemoveRestorePoint, тип 12 — как при создании).
    // Требуются права администратора; на части систем API может вернуть ошибку — тогда
    // точка остаётся в системном хранилище (ограничение документировано в шапке класса),
    // но из store приложения она всё равно выписывается.
    private void TryDeleteSystemRestorePoint(AppRestorePointRecord record)
    {
        try
        {
            var code = SRRemoveRestorePoint((uint)record.SequenceNumber, ModifySettingsRestorePointType);
            if (code == 0)
            {
                _logger.Info($"RP | deleted stale point | seq={record.SequenceNumber}");
            }
            else
            {
                _logger.Warn($"RP | SRRemoveRestorePoint rc={code} | seq={record.SequenceNumber} (осталась в системном хранилище)");
            }
        }
        catch (Exception exception)
        {
            _logger.Warn($"RP | SRRemoveRestorePoint failed | seq={record.SequenceNumber} | {exception.Message}");
        }
    }
}
