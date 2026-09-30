using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using SCU.Common;

namespace SCU.Infrastructure.Windows.RegistryAccess;

// Одно реестровое изменение. OffValue — «отключено» (пишем), RestoreValue — «включено»:
// null означает «удалить параметр» (для policy-ключей), иначе — записать значение по умолчанию.
public sealed record RegistryTweak(
    RegistryHive Hive,
    string SubKey,
    string ValueName,
    RegistryValueKind Kind,
    object OffValue,
    object? RestoreValue);

// Снимок исходного состояния одного значения для JSON-резерва.
public sealed record RegistryValueSnapshotDto
{
    [JsonPropertyName("subKey")] public string SubKey { get; set; } = string.Empty;
    [JsonPropertyName("valueName")] public string ValueName { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public int Kind { get; set; }
    [JsonPropertyName("exists")] public bool Exists { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
}

// Реестровые операции с резервом и верификацией: backup -> change -> verify.
// Аналог reg add / reg delete из Utilities.bat, но через Microsoft.Win32.Registry.
public sealed class RegistryHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Logger _logger;

    public RegistryHelper(Logger logger)
    {
        _logger = logger;
    }

    // Записывает OffValue для всех твиков, предварительно сохранив исходные значения в backupFile.
    // Повторный вызов не перезаписывает существующий резерв: первое сохранение — исходник.
    // Сбой в середине списка не оставляет реестр «наполовину применённым»: перед каждой
    // записью снимается текущее значение, применённое подмножество откатывается по снимкам.
    public Result Apply(IReadOnlyList<RegistryTweak> tweaks, string backupFile)
    {
        if (!File.Exists(backupFile))
        {
            var backup = BackupValues(tweaks, backupFile);
            if (!backup.IsSuccess)
            {
                return backup;
            }
        }

        var applied = new List<AppliedSnapshot>(tweaks.Count);
        foreach (var tweak in tweaks)
        {
            var previous = ReadCurrent(tweak);
            if (!previous.Success)
            {
                // Fail-closed: нечитаемое значение нельзя помечать «отсутствовавшим» —
                // иначе откат удалит живое значение пользователя. Прерываем до записи.
                RollbackApplied(applied);
                return Result.Failure(
                    $"Не удалось прочитать текущее значение {Label(tweak)}: применение прервано, "
                    + "чтобы откат не уничтожил исходное состояние. Применённая часть твиков отменена (откат по снимкам).");
            }

            var writeResult = WriteValue(tweak.Hive, tweak.SubKey, tweak.ValueName, tweak.Kind, tweak.OffValue);
            if (!writeResult.IsSuccess)
            {
                RollbackApplied(applied);
                return Result.Failure(writeResult.Message + " Применённая часть твиков отменена (откат по снимкам).");
            }

            applied.Add(new AppliedSnapshot(tweak, previous.Exists, previous.Kind, previous.Value));
            _logger.Info($"REG | set | {Label(tweak)} = {tweak.OffValue}");
        }

        var verify = VerifyApplied(tweaks);
        if (!verify.IsSuccess)
        {
            RollbackApplied(applied);
            return Result.Failure(verify.Message + " Применённая часть твиков отменена (откат по снимкам).");
        }

        return verify;
    }

    private readonly record struct AppliedSnapshot(
        RegistryTweak Tweak,
        bool Existed,
        RegistryValueKind? Kind,
        object? Value);

    private (bool Success, bool Exists, RegistryValueKind? Kind, object? Value) ReadCurrent(RegistryTweak tweak)
    {
        // Success=false отличает ошибку чтения (доступ запрещён, гонка) от «значения нет»:
        // только при Success=true снимок годен для отката.
        try
        {
            using var key = OpenKey(tweak.Hive, tweak.SubKey, writable: false);
            var value = key?.GetValue(tweak.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                // Раздел отсутствует или параметра нет — OpenSubKey при отказе в доступе
                // бросает исключение, поэтому null здесь означает честное «не существует».
                return (true, false, null, null);
            }

            return (true, true, key!.GetValueKind(tweak.ValueName), value);
        }
        catch (Exception exception)
        {
            _logger.Warn($"REG | read current failed | {Label(tweak)} | {exception.Message}");
            return (false, false, null, null);
        }
    }

    // Откат применённого подмножества по снимкам «до записи». Не реентерабельно
    // с Apply-списком: ошибки отката логируются и не маскируют исходную неудачу.
    private void RollbackApplied(List<AppliedSnapshot> applied)
    {
        foreach (var snapshot in applied)
        {
            var tweak = snapshot.Tweak;
            var rollbackResult = snapshot.Existed && snapshot.Kind is not null
                ? WriteValue(tweak.Hive, tweak.SubKey, tweak.ValueName, snapshot.Kind.Value, snapshot.Value!)
                : DeleteValue(tweak.Hive, tweak.SubKey, tweak.ValueName);

            if (rollbackResult.IsSuccess)
            {
                _logger.Info($"REG | rollback | {Label(tweak)}");
            }
            else
            {
                _logger.Error($"REG | rollback FAILED | {Label(tweak)} | {rollbackResult.Message}");
            }
        }
    }

    // Возвращает значения из резерва: существовавшие — записывает, отсутствовавшие — удаляет.
    public Result Restore(IReadOnlyList<RegistryTweak> tweaks, string backupFile)
    {
        if (!File.Exists(backupFile))
        {
            return Result.Failure("Файл резерва реестра не найден.", 2);
        }

        List<RegistryValueSnapshotDto> snapshots;
        try
        {
            snapshots = JsonSerializer.Deserialize<List<RegistryValueSnapshotDto>>(
                File.ReadAllText(backupFile)) ?? [];
        }
        catch (Exception exception)
        {
            return Result.Failure("Резерв реестра повреждён: " + exception.Message);
        }

        var skipped = new List<string>();
        // Уже восстановленные твики: до Restore реестр был в состоянии «отключено» (OffValue),
        // поэтому при сбое на середине возвращаем именно OffValue, а не бросаем на полпути.
        var restored = new List<RegistryTweak>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var tweak = ResolveTweak(snapshots, snapshot, tweaks);
            if (tweak is null)
            {
                // Значение есть в резерве, но нет в текущем списке твиков — восстанавливать
                // нечем. Не молчим: попадает в итоговое сообщение и лог.
                skipped.Add($"{snapshot.SubKey}\\{snapshot.ValueName}");
                continue;
            }

            if (!snapshot.Exists)
            {
                var deleteResult = DeleteValue(tweak.Hive, snapshot.SubKey, snapshot.ValueName);
                if (!deleteResult.IsSuccess)
                {
                    RollbackRestore(restored);
                    return deleteResult;
                }

                restored.Add(tweak);
                _logger.Info($"REG | del | {snapshot.SubKey} \\ {snapshot.ValueName}");
                continue;
            }

            var kind = (RegistryValueKind)snapshot.Kind;
            // DecodeValue для Binary идёт через FromBase64String и падает FormatException
            // на повреждённом резерве — прерываем Restore понятной ошибкой, а не исключением.
            object? value;
            try
            {
                value = DecodeValue(kind, snapshot.Data);
            }
            catch (Exception exception)
            {
                RollbackRestore(restored);
                return Result.Failure(
                    $"Резерв {snapshot.SubKey}\\{snapshot.ValueName}: данные повреждены ({exception.Message}).");
            }

            if (value is null)
            {
                RollbackRestore(restored);
                return Result.Failure($"Резерв {snapshot.SubKey}\\{snapshot.ValueName}: не удалось прочитать значение.");
            }

            var writeResult = WriteValue(tweak.Hive, snapshot.SubKey, snapshot.ValueName, kind, value);
            if (!writeResult.IsSuccess)
            {
                RollbackRestore(restored);
                return writeResult;
            }

            restored.Add(tweak);
            _logger.Info($"REG | restore | {snapshot.SubKey} \\ {snapshot.ValueName} = {snapshot.Data}");
        }

        if (skipped.Count > 0)
        {
            _logger.Warn("REG | restore skipped | " + string.Join("; ", skipped));
            return Result.Success(
                "Реестр восстановлен из резерва. Пропущены значения, отсутствующие в текущем списке твиков: "
                + string.Join("; ", skipped) + ".");
        }

        return Result.Success("Реестр восстановлен из резерва.");
    }

    // Откат частично выполненного Restore: возвращаем значения в состояние «отключено» (OffValue),
    // в котором реестр находился до восстановления. Ошибки отката логируются, не маскируя исходную.
    private void RollbackRestore(List<RegistryTweak> restored)
    {
        foreach (var tweak in restored)
        {
            var rollbackResult = WriteValue(tweak.Hive, tweak.SubKey, tweak.ValueName, tweak.Kind, tweak.OffValue);
            if (rollbackResult.IsSuccess)
            {
                _logger.Info($"REG | restore rollback | {Label(tweak)}");
            }
            else
            {
                _logger.Error($"REG | restore rollback FAILED | {Label(tweak)} | {rollbackResult.Message}");
            }
        }
    }

    // Возврат к заводскому состоянию без резерва: значения удаляются (RestoreValue=null-семантика).
    // Нужно для «Включить», когда отключение делали не через приложение и резерва нет.
    public Result ResetToDefault(IReadOnlyList<RegistryTweak> tweaks)
    {
        var deleted = 0;
        foreach (var tweak in tweaks)
        {
            var deleteResult = DeleteValue(tweak.Hive, tweak.SubKey, tweak.ValueName);
            if (!deleteResult.IsSuccess)
            {
                return deleteResult;
            }

            deleted++;
            _logger.Info($"REG | default | {Label(tweak)} удалено");
        }

        return Result.Success($"Возвращено к заводским значениям ({deleted}).");
    }

    // Верификация: все твики в состоянии «отключено»?
    public bool IsApplied(IReadOnlyList<RegistryTweak> tweaks)
    {
        foreach (var tweak in tweaks)
        {
            using var key = OpenKey(tweak.Hive, tweak.SubKey, writable: false);
            if (key is null)
            {
                return false;
            }

            var current = key.GetValue(tweak.ValueName);
            if (current is null || !Normalize(current).Equals(Normalize(tweak.OffValue), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private Result BackupValues(IReadOnlyList<RegistryTweak> tweaks, string backupFile)
    {
        // Чтение и запись резерва под общим try: значение может исчезнуть между
        // GetValue и GetValueKind — это ошибка резерва, а не падение Apply.
        try
        {
            var snapshots = new List<RegistryValueSnapshotDto>(tweaks.Count);
            foreach (var tweak in tweaks)
            {
                using var key = OpenKey(tweak.Hive, tweak.SubKey, writable: false);
                // DoNotExpandEnvironmentNames: иначе REG_EXPAND_SZ сохраняется уже
                // развёрнутым и %env%-переменные теряются при восстановлении.
                // Отказ в доступе здесь бросает исключение и валит весь резерв (fail-closed),
                // а не записывает Exists=false: null от GetValue/отсутствие раздела — единственный
                // путь к Exists=false.
                var value = key?.GetValue(
                    tweak.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is null)
                {
                    snapshots.Add(new RegistryValueSnapshotDto
                    {
                        SubKey = tweak.SubKey,
                        ValueName = tweak.ValueName,
                        Kind = (int)tweak.Kind,
                        Exists = false
                    });
                    continue;
                }

                snapshots.Add(new RegistryValueSnapshotDto
                {
                    SubKey = tweak.SubKey,
                    ValueName = tweak.ValueName,
                    Kind = (int)key!.GetValueKind(tweak.ValueName),
                    Exists = true,
                    Data = EncodeValue(value)
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.WriteAllText(backupFile, JsonSerializer.Serialize(snapshots, JsonOptions));
            _logger.Info($"REG | backup | {snapshots.Count} значений -> {backupFile}");
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось сохранить резерв реестра: " + exception.Message);
        }

        return Result.Success("Резерв сохранён.");
    }

    private Result VerifyApplied(IReadOnlyList<RegistryTweak> tweaks)
    {
        var mismatched = new List<string>();
        foreach (var tweak in tweaks)
        {
            using var key = OpenKey(tweak.Hive, tweak.SubKey, writable: false);
            var current = key?.GetValue(tweak.ValueName);
            if (current is null || !Normalize(current).Equals(Normalize(tweak.OffValue), StringComparison.Ordinal))
            {
                mismatched.Add(Label(tweak));
            }
        }

        if (mismatched.Count > 0)
        {
            _logger.Error("REG | verify FAILED | " + string.Join("; ", mismatched));
            return Result.Failure("Параметры не применились: " + string.Join("; ", mismatched));
        }

        _logger.Info($"REG | verify ok | {tweaks.Count} значений");
        return Result.Success("Все параметры применены и проверены.");
    }

    private Result WriteValue(RegistryHive hive, string subKey, string valueName, RegistryValueKind kind, object value)
    {
        try
        {
            // Отсутствующий ключ создаётся в ТОМ ЖЕ hive: раньше fallback уходил в CurrentUser,
            // из-за чего HKLM-policy значения писались не туда и не проходили верификацию.
            using var key = OpenKey(hive, subKey, writable: true);
            if (key is not null)
            {
                key.SetValue(valueName, value, kind);
                return Result.Success();
            }

            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var created = baseKey.CreateSubKey(subKey, writable: true);
            created.SetValue(valueName, value, kind);
            return Result.Success();
        }
        catch (Exception exception)
        {
            _logger.Error($"REG | set failed | {subKey} \\ {valueName} | {exception.Message}");
            return Result.Failure($"Не удалось записать {subKey}\\{valueName}: {exception.Message}");
        }
    }

    private Result DeleteValue(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var key = OpenKey(hive, subKey, writable: true);
            if (key is null)
            {
                // Отличаем отсутствующий раздел от отказа в доступе.
                using var readOnlyKey = OpenKey(hive, subKey, writable: false);
                return readOnlyKey is null
                    ? Result.Success()
                    : Result.Failure($"Не удалось открыть {Label(hive, subKey, valueName)} на запись.");
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
            var remaining = key.GetValue(
                valueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (remaining is not null)
            {
                return Result.Failure($"Не удалось удалить {Label(hive, subKey, valueName)}: значение осталось в реестре.");
            }

            return Result.Success();
        }
        catch (Exception exception)
        {
            _logger.Error($"REG | delete failed | {Label(hive, subKey, valueName)} | {exception.Message}");
            return Result.Failure($"Не удалось удалить {Label(hive, subKey, valueName)}: {exception.Message}");
        }
    }

    private static string Label(RegistryHive hive, string subKey, string valueName) =>
        $"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{subKey}\\{valueName}";

    private static RegistryKey? OpenKey(RegistryHive hive, string subKey, bool writable)
    {
        // Базовый ключ держит собственный хэндл — диспозим, чтобы не течёт на каждый вызов.
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        return baseKey.OpenSubKey(subKey, writable);
    }

    private static string Label(RegistryTweak tweak) =>
        $"{(tweak.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{tweak.SubKey}\\{tweak.ValueName}";

    private static RegistryTweak? ResolveTweak(
        IReadOnlyList<RegistryValueSnapshotDto> snapshots,
        RegistryValueSnapshotDto snapshot,
        IReadOnlyList<RegistryTweak> tweaks)
    {
        var match = tweaks.FirstOrDefault(t => t.SubKey == snapshot.SubKey && t.ValueName == snapshot.ValueName);
        if (match is not null)
        {
            return match;
        }

        // Значение есть в резерве, но нет в текущем списке твиков — восстанавливать нечем.
        return null;
    }

    internal static string EncodeValue(object value) => value switch
    {
        int intValue => intValue.ToString(CultureInfo.InvariantCulture),
        long longValue => longValue.ToString(CultureInfo.InvariantCulture),
        string stringValue => stringValue,
        // REG_MULTI_SZ: JSON-массив — сохраняет элементы с переводами строк,
        // которые Join("\n") превращал в лишние записи. Чтение понимает оба формата.
        string[] multiValue => JsonSerializer.Serialize(multiValue),
        byte[] binaryValue => Convert.ToBase64String(binaryValue),
        _ => value.ToString() ?? string.Empty
    };

    // Возвращает null при нечитаемых данных — Restore прервётся с ошибкой,
    // а не запишет 0 в живое значение реестра.
    internal static object? DecodeValue(RegistryValueKind kind, string? data)
    {
        if (data is null)
        {
            return null;
        }

        return kind switch
        {
            // DWord беззнаковый (0..uint.MaxValue), но SetValue принимает int —
            // парсим в long и переносим биты: "4294967295" -> unchecked((int)-1) = 0xFFFFFFFF.
            RegistryValueKind.DWord => long.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dwordValue)
                    && dwordValue is >= int.MinValue and <= uint.MaxValue
                ? unchecked((int)dwordValue)
                : null,
            RegistryValueKind.QWord => long.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)
                ? longValue
                : null,
            RegistryValueKind.String or RegistryValueKind.ExpandString => data,
            RegistryValueKind.Binary => Convert.FromBase64String(data),
            RegistryValueKind.MultiString => DecodeMultiString(data),
            _ => data
        };
    }

    // REG_MULTI_SZ читается в двух форматах: новый — JSON-массив (элементы с
    // переводами строк сохраняются), старый (резервы до изменения) — строки,
    // склеенные "\n". Старый формат оставался только как fallback: JSON-парс
    // "a\nb" даёт ошибку, и значение уходит в Split('\n').
    private static object? DecodeMultiString(string data)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(data) ?? data.Split('\n');
        }
        catch (JsonException)
        {
            return data.Split('\n');
        }
    }

    private static string Normalize(object value) => value switch
    {
        int intValue => intValue.ToString(CultureInfo.InvariantCulture),
        long longValue => longValue.ToString(CultureInfo.InvariantCulture),
        string stringValue => stringValue,
        string[] multiValue => string.Join("\n", multiValue),
        byte[] binaryValue => Convert.ToBase64String(binaryValue),
        _ => value.ToString() ?? string.Empty
    };
}

