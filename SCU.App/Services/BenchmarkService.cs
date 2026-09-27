using SCU.Common;
using SCU.Models;
using SCU.Models.Benchmark;

namespace SCU.Services;

// Расчёт индекса состояния системы по снимку SystemSnapshot.
//
// Принципы (ТЗ «Бэнчмарк»):
//  - индекс оценивает измеримое соответствие системы целевым правилам SCU,
//    а не количество функций программы;
//  - Unknown (не прочитано) и NotApplicable исключаются из расчёта и снижают
//    покрытие, но не сам индекс;
//  - универсальный профиль содержит только универсально интерпретируемые
//    проверки: отключённые службы/задачи/обновления — не «плохо», а факт;
//  - потенциал = сумма доступных баллов соответствия по неполным метрикам,
//    это не обещание ускорения.
//
// Сервис чистый: на вход снимок, на выход BenchmarkResult; локализация
// подписей — на слое отображения (ключи идентификаторов стабильны).
public sealed class BenchmarkService
{
    public const int AlgorithmVersion = 1;

    // Ключи категорий (стабильные; локализуются отображением).
    public const string CategorySystem = "system";
    public const string CategoryDisk = "disk";
    public const string CategoryStartup = "startup";
    public const string CategoryServices = "services";
    public const string CategoryTasks = "tasks";
    public const string CategoryPower = "power";
    public const string CategorySecurity = "security";
    public const string CategoryPrivacy = "privacy";
    public const string CategoryUpdates = "updates";
    public const string CategoryNetwork = "network";

    // Область сканирования -> зависимые от неё метрики.
    private static readonly Dictionary<string, string[]> AreaMetricIds = new(StringComparer.Ordinal)
    {
        [SystemStateService.AreaSystem] = ["system.read"],
        [SystemStateService.AreaDisk] = ["disk.free_system"],
        [SystemStateService.AreaStartup] = ["startup.count"],
        [SystemStateService.AreaServices] = ["services.readable"],
        [SystemStateService.AreaTasks] = ["tasks.readable"],
        [SystemStateService.AreaPower] = ["power.readable"],
        [SystemStateService.AreaSecurity] = ["security.uac_disabled"],
        [SystemStateService.AreaPrivacy] = ["privacy.enabled"],
        [SystemStateService.AreaUpdates] = ["updates.readable"],
        [SystemStateService.AreaNetwork] = ["network.readable"]
    };

    public BenchmarkResult Evaluate(SystemSnapshot snapshot)
    {
        var metrics = new List<BenchmarkMetricResult>
        {
            EvaluateSystemRead(snapshot),
            EvaluateDiskFree(snapshot),
            EvaluateStartupCount(snapshot),
            EvaluateServicesReadable(snapshot),
            EvaluateTasksReadable(snapshot),
            EvaluatePowerReadable(snapshot),
            EvaluateUacDisabled(snapshot),
            EvaluatePrivacyEnabled(snapshot),
            EvaluateUpdatesReadable(snapshot),
            EvaluateNetworkReadable(snapshot)
        };

        // Области, чтение которых не удалось, переводят их метрики в Unknown —
        // даже если в снимке остались значения от предыдущих источников:
        // показывать «прочитано» по данным, которые не удалось прочитать, нельзя.
        foreach (var (area, error) in snapshot.AreaErrors)
        {
            if (!AreaMetricIds.TryGetValue(area, out var metricIds))
            {
                continue;
            }

            for (var i = 0; i < metrics.Count; i++)
            {
                var metric = metrics[i];
                if (metricIds.Contains(metric.Id) && metric.State == BenchmarkMetricState.Ok)
                {
                    metrics[i] = metric with { State = BenchmarkMetricState.Unknown, Note = error };
                }
            }
        }

        var known = metrics.Where(m => m.State == BenchmarkMetricState.Ok).ToList();
        var weightKnown = known.Sum(m => m.Weight);
        var weightApplicable = metrics.Sum(m => m.Weight);
        var index = weightKnown > 0
            ? (int)Math.Round(100.0 * known.Sum(m => m.Weight * m.Conformity) / weightKnown)
            : 0;
        var coverage = weightApplicable > 0
            ? (int)Math.Round(100.0 * weightKnown / weightApplicable)
            : 0;
        var potential = (int)Math.Round(known.Sum(m => m.Weight * (1.0 - m.Conformity)));

        var categories = metrics
            .GroupBy(m => m.Category, StringComparer.Ordinal)
            .Select(group =>
            {
                var groupKnown = group.Where(m => m.State == BenchmarkMetricState.Ok).ToList();
                var groupWeightKnown = groupKnown.Sum(m => m.Weight);
                return new BenchmarkCategoryResult(
                    group.Key,
                    groupWeightKnown,
                    groupWeightKnown > 0
                        ? (int)Math.Round(100.0 * groupKnown.Sum(m => m.Weight * m.Conformity) / groupWeightKnown)
                        : 0,
                    groupKnown.Count,
                    group.Count());
            })
            .OrderBy(category => category.Category, StringComparer.Ordinal)
            .ToList();

        return new BenchmarkResult(
            AlgorithmVersion,
            DateTime.Now,
            index,
            coverage,
            categories,
            metrics,
            potential,
            snapshot.AreaErrors.Keys.OrderBy(area => area, StringComparer.Ordinal).ToList());
    }

    // ===================== Метрики универсального профиля =====================

    private static BenchmarkMetricResult Metric(
        string id, string category, int weight,
        BenchmarkMetricState state, double conformity = 0,
        double? numericValue = null, string? textValue = null, string? note = null) =>
        new(id, category, weight, state, Math.Clamp(conformity, 0, 1), numericValue, textValue, note);

    // Система: базовые сведения прочитаны.
    private static BenchmarkMetricResult EvaluateSystemRead(SystemSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.WindowsVersion)
            ? Metric("system.read", CategorySystem, 2, BenchmarkMetricState.Unknown)
            : Metric("system.read", CategorySystem, 2, BenchmarkMetricState.Ok, 1, textValue: snapshot.WindowsVersion);

    // Диск: свободное место на системном томе. Частичное соответствие ниже порога:
    // 10 ГБ — полное соответствие, 0 ГБ — ноль.
    private static BenchmarkMetricResult EvaluateDiskFree(SystemSnapshot snapshot)
    {
        var systemDisk = FindSystemDisk(snapshot);
        if (systemDisk is null)
        {
            return Metric("disk.free_system", CategoryDisk, 8, BenchmarkMetricState.Unknown,
                note: "системный диск не определён");
        }

        var conformity = DashboardThresholds.LowDiskFreeGb <= 0
            ? 1
            : systemDisk.FreeGb / DashboardThresholds.LowDiskFreeGb;
        return Metric("disk.free_system", CategoryDisk, 8, BenchmarkMetricState.Ok,
            conformity, numericValue: systemDisk.FreeGb);
    }

    // Автозагрузка: до 10 элементов — полное соответствие (порог DashboardThresholds),
    // каждый лишний — минус десятая. Строгой универсальной шкалы нет, это наблюдение.
    private static BenchmarkMetricResult EvaluateStartupCount(SystemSnapshot snapshot)
    {
        if (snapshot.StartupCount is not { } count)
        {
            return Metric("startup.count", CategoryStartup, 5, BenchmarkMetricState.Unknown);
        }

        var conformity = count <= DashboardThresholds.StartupAttentionCount
            ? 1.0
            : Math.Max(0.0, 1.0 - (count - DashboardThresholds.StartupAttentionCount) / 10.0);
        return Metric("startup.count", CategoryStartup, 5, BenchmarkMetricState.Ok,
            conformity, numericValue: count);
    }

    // Службы: метрика управляемости — все службы из контрольного набора читаются.
    // Отключённые службы не штрафуются (осознанная настройка).
    private static BenchmarkMetricResult EvaluateServicesReadable(SystemSnapshot snapshot)
    {
        if (snapshot.ServicesTotal is not { } total || total == 0)
        {
            return Metric("services.readable", CategoryServices, 4, BenchmarkMetricState.Unknown);
        }

        var readable = (snapshot.ServicesOk ?? 0) + (snapshot.ServicesChanged ?? 0);
        return Metric("services.readable", CategoryServices, 4, BenchmarkMetricState.Ok,
            (double)readable / total, numericValue: readable, textValue: total.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Задачи: контрольный набор прочитан планировщиком.
    private static BenchmarkMetricResult EvaluateTasksReadable(SystemSnapshot snapshot) =>
        snapshot.TasksTotal is { } total && total > 0
            ? Metric("tasks.readable", CategoryTasks, 3, BenchmarkMetricState.Ok, 1,
                numericValue: total, textValue: (snapshot.TasksDisabled ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture))
            : Metric("tasks.readable", CategoryTasks, 3, BenchmarkMetricState.Unknown);

    // Питание: активная схема определена. Сама схема — не универсальная шкала.
    private static BenchmarkMetricResult EvaluatePowerReadable(SystemSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.ActivePlanGuid)
            ? Metric("power.readable", CategoryPower, 2, BenchmarkMetricState.Unknown)
            : Metric("power.readable", CategoryPower, 2, BenchmarkMetricState.Ok, 1,
                textValue: snapshot.ActivePlanGuid);

    // Безопасность: UAC отключён/ослаблен — меньше запросов повышения прав
    // (отключенный UAC считается плюсом, стандартный уровень — отклонение).
    private static BenchmarkMetricResult EvaluateUacDisabled(SystemSnapshot snapshot) =>
        snapshot.UacState switch
        {
            SystemStateService.UacWeakened => Metric("security.uac_disabled", CategorySecurity, 8,
                BenchmarkMetricState.Ok, 1, textValue: "weakened"),
            SystemStateService.UacStandard => Metric("security.uac_disabled", CategorySecurity, 8,
                BenchmarkMetricState.Ok, 0, textValue: "standard"),
            _ => Metric("security.uac_disabled", CategorySecurity, 8, BenchmarkMetricState.Unknown)
        };

    // Приватность: доля ВКЛЮЧЁННЫХ переключателей (частичное соответствие).
    // Включённый тумблер категории — плюс; применённые (отключенные) категории
    // очков не дают.
    private static BenchmarkMetricResult EvaluatePrivacyEnabled(SystemSnapshot snapshot)
    {
        if (snapshot.PrivacyTotal is not { } total || total == 0 || snapshot.PrivacyAppliedCount is not { } applied)
        {
            return Metric("privacy.enabled", CategoryPrivacy, 6, BenchmarkMetricState.Unknown);
        }

        var enabled = Math.Max(0, total - applied);
        return Metric("privacy.enabled", CategoryPrivacy, 6, BenchmarkMetricState.Ok,
            (double)enabled / total, numericValue: enabled,
            textValue: total.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Обновления: состояние определено (блокировка/пауза — осознанные настройки, не штраф).
    private static BenchmarkMetricResult EvaluateUpdatesReadable(SystemSnapshot snapshot) =>
        snapshot.UpdateBlocked is null && snapshot.UpdatePaused is null
            ? Metric("updates.readable", CategoryUpdates, 2, BenchmarkMetricState.Unknown)
            : Metric("updates.readable", CategoryUpdates, 2, BenchmarkMetricState.Ok, 1);

    // Сеть: сетевая сводка прочитана. Конкретные значения TCP — предмет профиля, не универсала.
    private static BenchmarkMetricResult EvaluateNetworkReadable(SystemSnapshot snapshot) =>
        snapshot.Network is null
            ? Metric("network.readable", CategoryNetwork, 2, BenchmarkMetricState.Unknown)
            : Metric("network.readable", CategoryNetwork, 2, BenchmarkMetricState.Ok, 1);

    // Системный диск = диск с корнем каталога Windows (как в RecommendationEngine).
    private static DiskSnapshot? FindSystemDisk(SystemSnapshot snapshot)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
        if (string.IsNullOrEmpty(systemRoot))
        {
            return null;
        }

        return snapshot.Disks.FirstOrDefault(disk =>
            string.Equals(disk.Letter.TrimEnd('\\'), systemRoot, StringComparison.OrdinalIgnoreCase));
    }
}
