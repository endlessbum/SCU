namespace SCU.Infrastructure.Windows.Power;

using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

// П. 13 аудита: зона файловых твиков PowerService (8.3, Last Access,
// Prefetcher, SysMain): реестр + fsutil. PowerService сохраняет публичный
// фасад — вызывающие и тесты не меняются.
public sealed class FileSystemTweaks
{
    private readonly Logger _logger;
    private readonly LongProcessRunner _runner;
    private readonly RegistryHelper _registry;

    public FileSystemTweaks(Logger logger, LongProcessRunner runner, RegistryHelper registry)
    {
        _logger = logger;
        _runner = runner;
        _registry = registry;
    }

    private static string BackupPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "power", fileName);

    private static IReadOnlyList<RegistryTweak> ShortNames8dot3Tweak =>
    [
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\FileSystem",
            "NtfsDisable8dot3NameCreation",
            RegistryValueKind.DWord,
            1, 0)
    ];

    private static IReadOnlyList<RegistryTweak> LastAccessTweak =>
    [
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\FileSystem",
            "NtfsDisableLastAccessUpdate",
            RegistryValueKind.DWord,
            1, 0)
    ];

    private static IReadOnlyList<RegistryTweak> PrefetcherTweak =>
    [
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
            "EnablePrefetcher",
            RegistryValueKind.DWord,
            0, 3)
    ];


    public Result Set8dot3NamesEnabled(bool enable)
    {
        var tweak = ShortNames8dot3Tweak[0] with { OffValue = enable ? 0 : 1 };
        var result = _registry.Apply([tweak], BackupPath("8dot3names.json"));
        return result.IsSuccess
            ? Result.Success(enable
                ? "Создание имён 8.3 включено (перезагрузка не требуется для новых файлов)."
                : "Создание имён 8.3 отключено (полностью применится после перезагрузки).")
            : result;
    }

    public Result SetLastAccessEnabled(bool enable)
    {
        var tweak = LastAccessTweak[0] with { OffValue = enable ? 0 : 1 };
        var result = _registry.Apply([tweak], BackupPath("lastaccess.json"));
        if (!result.IsSuccess)
            return result;

        // Повторное чтение: Success только если значение реально совпало с ожидаемым.
        var info = GetLastAccessInfo();
        var expectedDisabled = !enable;
        var actualDisabled = info.State == SystemSettingState.Disabled;
        if (info.State is SystemSettingState.Unknown)
        {
            return Result.Failure("Параметр записан, но состояние Last Access не удалось подтвердить чтением.");
        }

        if (actualDisabled != expectedDisabled)
        {
            return Result.Failure(
                "Параметр Last Access записан, но effective state не совпал с ожидаемым. " +
                "Возможно, требуется перезагрузка или права администратора.");
        }

        return Result.Success(enable
            ? "Учёт времени последнего доступа включён."
            : "Учёт времени последнего доступа отключён.");
    }

    public Result SetPrefetcherEnabled(bool enable)
    {
        var tweak = PrefetcherTweak[0] with { OffValue = enable ? 3 : 0 };
        var result = _registry.Apply([tweak], BackupPath("prefetcher.json"));
        return result.IsSuccess
            ? Result.Success(enable ? "Prefetcher включён." : "Prefetcher отключён.")
            : result;
    }

    /// <summary>
    /// Полная модель 8.3: глобальный режим 0/1/2/3 и effective state.
    /// Не сводит mode 2/3 к простому bool.
    /// </summary>
    public static ShortNamesInfo GetShortNamesInfo()
    {
        int? raw;
        try
        {
            raw = ReadFileSystemDWord("NtfsDisable8dot3NameCreation");
        }
        catch
        {
            return new ShortNamesInfo(SystemSettingState.Unknown, ShortNameGlobalMode.Unknown, null);
        }

        if (raw is null)
        {
            // По умолчанию Windows: зависит от версии; на современных клиентах часто 2 (per-volume).
            return new ShortNamesInfo(SystemSettingState.Unknown, ShortNameGlobalMode.Unknown, null);
        }

        var mode = MapShortNameGlobalMode(raw.Value);
        var effective = MapShortNameEffective(mode);
        return new ShortNamesInfo(effective, mode, VolumeStates: null);
    }

    /// <summary>
    /// Как GetShortNamesInfo, но для PerVolume/DisabledExceptSystem опрашивает тома через fsutil 8dot3name query.
    /// </summary>
    public async Task<Result<ShortNamesInfo>> GetShortNamesInfoAsync(CancellationToken ct = default)
    {
        var baseInfo = GetShortNamesInfo();
        if (baseInfo.GlobalMode is not (ShortNameGlobalMode.PerVolume or ShortNameGlobalMode.DisabledExceptSystem))
        {
            return Result<ShortNamesInfo>.Success(baseInfo);
        }

        try
        {
            var volumes = new List<(string Volume, bool Enabled)>();
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                ct.ThrowIfCancellationRequested();
                var root = drive.Name.TrimEnd('\\');
                var query = await _runner
                    .RunAsync("fsutil", ["8dot3name", "query", root], null, ct)
                    .ConfigureAwait(false);
                if (!query.IsSuccess || query.Value is null)
                    continue;

                var enabled = PowerParsers.TryParseVolumeStateEnabled(query.Value) ?? false;

                volumes.Add((root + "\\", enabled));
            }

            var effective = baseInfo.EffectiveState;
            if (volumes.Count > 0)
            {
                var allOn = volumes.All(v => v.Enabled);
                var allOff = volumes.All(v => !v.Enabled);
                effective = allOn ? SystemSettingState.Enabled
                    : allOff ? SystemSettingState.Disabled
                    : SystemSettingState.Mixed;
            }

            return Result<ShortNamesInfo>.Success(new ShortNamesInfo(
                effective,
                baseInfo.GlobalMode,
                volumes.Count > 0 ? volumes : null));
        }
        catch (OperationCanceledException)
        {
            return Result<ShortNamesInfo>.Failure("Отменено", -1);
        }
        catch
        {
            return Result<ShortNamesInfo>.Success(baseInfo);
        }
    }

    internal static ShortNameGlobalMode MapShortNameGlobalMode(int raw) =>
        raw switch
        {
            0 => ShortNameGlobalMode.EnabledForAll,
            1 => ShortNameGlobalMode.DisabledForAll,
            2 => ShortNameGlobalMode.PerVolume,
            3 => ShortNameGlobalMode.DisabledExceptSystem,
            _ => ShortNameGlobalMode.Unknown
        };

    internal static SystemSettingState MapShortNameEffective(ShortNameGlobalMode mode) =>
        mode switch
        {
            ShortNameGlobalMode.EnabledForAll => SystemSettingState.Enabled,
            ShortNameGlobalMode.DisabledForAll => SystemSettingState.Disabled,
            ShortNameGlobalMode.PerVolume => SystemSettingState.Mixed,
            ShortNameGlobalMode.DisabledExceptSystem => SystemSettingState.PartiallyEnabled,
            _ => SystemSettingState.Unknown
        };

    /// <summary>Совместимость: true только при однозначно «включено для всех» (mode 0).</summary>
    public static bool Is8dot3Enabled()
    {
        var info = GetShortNamesInfo();
        return info.GlobalMode == ShortNameGlobalMode.EnabledForAll;
    }

    /// <summary>
    /// Last Access: NtfsDisableLastAccessUpdate (и при возможности fsutil).
    /// Значения (Microsoft): 0 = user, updates on; 1 = user, updates off;
    /// 2 = system managed, updates on; 3 = system managed, updates off.
    /// Младший бит 1 → updates disabled. Не сводим ошибку к false.
    /// </summary>
    public static LastAccessInfo GetLastAccessInfo()
    {
        int? value;
        try
        {
            value = ReadFileSystemDWord("NtfsDisableLastAccessUpdate");
        }
        catch
        {
            return new LastAccessInfo(SystemSettingState.Unknown, null, RequiresReboot: false);
        }

        if (value is null)
            return new LastAccessInfo(SystemSettingState.Unknown, null, RequiresReboot: false);

        // Младший бит: 0 = last access updates enabled, 1 = disabled.
        var disabled = (value.Value & 1) != 0;
        // На современных Windows изменение обычно применяется без reboot;
        // RequiresReboot оставляем false при чистом чтении. После Set* UI
        // может выставить PendingReboot, если нужно подчеркнуть осторожность.
        return new LastAccessInfo(
            disabled ? SystemSettingState.Disabled : SystemSettingState.Enabled,
            value,
            RequiresReboot: false);
    }

    /// <summary>
    /// Дополняет GetLastAccessInfo опросом <c>fsutil behavior query disablelastaccess</c>.
    /// Если registry и fsutil расходятся — PendingReboot / Mixed.
    /// </summary>
    public async Task<Result<LastAccessInfo>> GetLastAccessInfoAsync(CancellationToken ct = default)
    {
        var baseInfo = GetLastAccessInfo();
        try
        {
            var fsutil = await _runner
                .RunAsync("fsutil", ["behavior", "query", "disablelastaccess"], null, ct)
                .ConfigureAwait(false);

            if (!fsutil.IsSuccess || string.IsNullOrWhiteSpace(fsutil.Value))
            {
                // Registry-only уже есть; fsutil недоступен — не превращаем в Unknown.
                return Result<LastAccessInfo>.Success(baseInfo);
            }

            var fsutilValue = PowerParsers.TryParseFsutilValue(fsutil.Value);

            if (fsutilValue is null)
                return Result<LastAccessInfo>.Success(baseInfo);

            var regDisabled = baseInfo.RawValue is int rv && (rv & 1) != 0;
            var fsDisabled = (fsutilValue.Value & 1) != 0;

            if (baseInfo.State == SystemSettingState.Unknown)
            {
                return Result<LastAccessInfo>.Success(new LastAccessInfo(
                    fsDisabled ? SystemSettingState.Disabled : SystemSettingState.Enabled,
                    fsutilValue,
                    RequiresReboot: false));
            }

            if (regDisabled != fsDisabled)
            {
                // Реестр уже новый, runtime (fsutil) ещё старый — типичный PendingReboot.
                return Result<LastAccessInfo>.Success(new LastAccessInfo(
                    SystemSettingState.PendingReboot,
                    baseInfo.RawValue ?? fsutilValue,
                    RequiresReboot: true));
            }

            return Result<LastAccessInfo>.Success(baseInfo);
        }
        catch (OperationCanceledException)
        {
            return Result<LastAccessInfo>.Failure("Отменено", -1);
        }
        catch
        {
            return Result<LastAccessInfo>.Success(baseInfo);
        }
    }

    public static bool IsLastAccessEnabled()
    {
        var info = GetLastAccessInfo();
        return info.State == SystemSettingState.Enabled;
    }

    public static bool? TryIsPrefetcherEnabled()
    {
        var value = ReadPrefetcherDWord("EnablePrefetcher");
        if (value is null) return null;
        return value != 0;
    }

    public static bool IsPrefetcherEnabled() => TryIsPrefetcherEnabled() ?? true;

    public static bool? TryIsSysMainEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SysMain");
            return key?.GetValue("Start") switch
            {
                int start => start != 4,
                null => null,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public static bool IsSysMainEnabled() => TryIsSysMainEnabled() ?? true;

    private static int? ReadFileSystemDWord(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue(valueName) as int?;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadPrefetcherDWord(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            return key?.GetValue(valueName) as int?;
        }
        catch
        {
            return null;
        }
    }
}