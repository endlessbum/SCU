using System.Globalization;
using System.Management;
using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.Infrastructure.Windows.Power;

// Раздел 8 «Utilities.bat» — питание, память и CPU.
// powercfg/bcdedit — через LongProcessRunner, реестр — через RegistryHelper, WMI — напрямую.
public sealed partial class PowerService
{
    public static readonly string HighPerformancePlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public static readonly string UltimatePerformancePlanGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public static readonly string BalancedPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    // Штатная «Экономия энергии» и план Bitsum Highest Performance (создаётся Process Lasso /
    // ParkControl; если схемы нет — powercfg -duplicatescheme по шаблону, как для остальных).
    public static readonly string PowerSaverPlanGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public static readonly string BitsumHighestPerformancePlanGuid = "19cfaaf6-8487-4fd0-809e-3b5b1ec82302";

    // Стабильные GUID для копий скрытых/сторонних шаблонов. powercfg позволяет
    // передать destination GUID в /duplicatescheme, поэтому повторные запуски
    // не порождают новые схемы даже после удаления/повреждения plans.json.
    // Публичные: VM подсвечивает чипы схем по GUID фактически активной копии.
    public const string UltimatePerformanceCopyGuid = "f16f20f1-4372-42ce-8601-8a3500053cb6";
    public const string BitsumHighestPerformanceCopyGuid = "bea1bc1e-5df4-4697-8ddb-44826e5b3fdc";

    private readonly Logger _logger;
    private readonly LongProcessRunner _runner;
    private readonly RegistryHelper _registry;

    public PowerService(Logger logger, LongProcessRunner runner, RegistryHelper registry)
    {
        _logger = logger;
        _runner = runner;
        _registry = registry;
    }

    // Только чтение: GUID активной схемы (powercfg /getactivescheme).
    public async Task<Result<string>> GetActivePlanGuidAsync(CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync("powercfg", ["/getactivescheme"], null, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result<string>.Failure(result.Message, result.Code);
        }

        var guid = LongProcessRunner.ExtractGuids(result.Value ?? string.Empty).FirstOrDefault();
        return guid is null
            ? Result<string>.Failure("powercfg не вернул GUID активной схемы.")
            : Result<string>.Success(guid);
    }

    private static string BackupPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "power", fileName);

    // Соответствие «GUID шаблона -> GUID созданной копии» (plans.json). Кэшируется на запуск;
    // повреждённый файл молча даёт пустой словарь — тогда просто создастся новая копия.
    private Dictionary<string, string>? _duplicatePlans;

    private static string? GetStableDuplicateGuid(string templateGuid)
    {
        if (string.Equals(templateGuid, UltimatePerformancePlanGuid, StringComparison.OrdinalIgnoreCase))
        {
            return UltimatePerformanceCopyGuid;
        }

        if (string.Equals(templateGuid, BitsumHighestPerformancePlanGuid, StringComparison.OrdinalIgnoreCase))
        {
            return BitsumHighestPerformanceCopyGuid;
        }

        return null;
    }

    private Dictionary<string, string> LoadDuplicatePlans()
    {
        if (_duplicatePlans is not null)
        {
            return _duplicatePlans;
        }

        try
        {
            var path = BackupPath("plans.json");
            _duplicatePlans = File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch
        {
            _duplicatePlans = [];
        }

        return _duplicatePlans;
    }

    private void SaveDuplicatePlans(Dictionary<string, string> plans)
    {
        try
        {
            var path = BackupPath("plans.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(plans));
            _duplicatePlans = plans;
        }
        catch (Exception exception)
        {
            _logger.Warn("POWER | plans.json save failed | " + exception.Message);
        }
    }
    // ===================== Фасад зоны файловых твиков (FileSystemTweaks, п. 13) =====================

    private FileSystemTweaks? _fileSystemTweaks;
    private FileSystemTweaks FileSystem => _fileSystemTweaks ??= new(_logger, _runner, _registry);

    public Result Set8dot3NamesEnabled(bool enable) => FileSystem.Set8dot3NamesEnabled(enable);
    public Result SetLastAccessEnabled(bool enable) => FileSystem.SetLastAccessEnabled(enable);
    public Result SetPrefetcherEnabled(bool enable) => FileSystem.SetPrefetcherEnabled(enable);
    public static ShortNamesInfo GetShortNamesInfo() => FileSystemTweaks.GetShortNamesInfo();
    public Task<Result<ShortNamesInfo>> GetShortNamesInfoAsync(CancellationToken ct = default) => FileSystem.GetShortNamesInfoAsync(ct);
    public static LastAccessInfo GetLastAccessInfo() => FileSystemTweaks.GetLastAccessInfo();
    public Task<Result<LastAccessInfo>> GetLastAccessInfoAsync(CancellationToken ct = default) => FileSystem.GetLastAccessInfoAsync(ct);
    public static bool Is8dot3Enabled() => FileSystemTweaks.Is8dot3Enabled();
    public static bool IsLastAccessEnabled() => FileSystemTweaks.IsLastAccessEnabled();
    public static bool? TryIsPrefetcherEnabled() => FileSystemTweaks.TryIsPrefetcherEnabled();
    public static bool IsPrefetcherEnabled() => FileSystemTweaks.IsPrefetcherEnabled();
    public static bool? TryIsSysMainEnabled() => FileSystemTweaks.TryIsSysMainEnabled();
    public static bool IsSysMainEnabled() => FileSystemTweaks.IsSysMainEnabled();
    internal static ShortNameGlobalMode MapShortNameGlobalMode(int raw) => FileSystemTweaks.MapShortNameGlobalMode(raw);
    internal static SystemSettingState MapShortNameEffective(ShortNameGlobalMode mode) => FileSystemTweaks.MapShortNameEffective(mode);
}
