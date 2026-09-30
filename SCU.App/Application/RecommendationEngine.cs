using System.Globalization;
using SCU.Models;

using SCU.Common;

namespace SCU.AppCore;

// Пороги правил Dashboard — в одном месте (MVP). Изменяются только здесь.
public static class DashboardThresholds
{
    public const double LowDiskFreeGb = 10;
    public const double LowDiskFreePercent = 10;
    public const int StartupAttentionCount = 10;
}

// Чистая логика рекомендаций MVP: вход — снимок, выход — объяснимые правила.
// Никаких score/процентов «ускорения»: каждое правило — факт из снимка против порога.
// Отличия предпочтений (активная схема питания, игровой профиль) проблемой не считаются.
public static class RecommendationEngine
{
    public static IReadOnlyList<Recommendation> Evaluate(SystemSnapshot snapshot)
    {
        var result = new List<Recommendation>();

        // Системный диск: мало свободного места (правило — только если данные известны).
        var systemDisk = FindSystemDisk(snapshot);
        if (systemDisk is not null)
        {
            var freePercent = systemDisk.UsedPercent is { } used ? 100.0 - used : (double?)null;
            if (systemDisk.FreeGb < DashboardThresholds.LowDiskFreeGb
                || (freePercent is not null && freePercent.Value < DashboardThresholds.LowDiskFreePercent))
            {
                // Занято известно из UsedPercent; если известен только free — считаем из него.
                var usedText = systemDisk.UsedPercent is { } usedValue
                    ? string.Format(CultureInfo.CurrentCulture, " (занято {0:0.0}%)", usedValue)
                    : string.Empty;
                result.Add(new Recommendation(
                    "disk.low_free",
                    "disk",
                    RecommendationSeverity.Warning,
                    L.T("Мало свободного места на системном диске"),
                    L.T(
                        "На диске {0} свободно {1:0.0} ГБ из {2:0.0} ГБ{3}. Освободить место можно в разделе очистки.",
                        systemDisk.Letter,
                        systemDisk.FreeGb,
                        systemDisk.TotalGb,
                        usedText),
                    L.T("Открыть очистку диска"),
                    3));
            }
        }

        // Автозагрузка: много элементов (наблюдение, не ошибка).
        if (snapshot.StartupCount is { } startupCount && startupCount > DashboardThresholds.StartupAttentionCount)
        {
            result.Add(new Recommendation(
                "startup.many",
                "startup",
                RecommendationSeverity.Info,
                L.T("Много элементов автозагрузки ({0})", startupCount),
                L.T(
                    "В автозагрузке {0} элементов (порог {1}). Это не ошибка, но каждая запись может замедлять запуск Windows.",
                    startupCount,
                    DashboardThresholds.StartupAttentionCount),
                L.T("Открыть автозагрузку"),
                7));
        }

        // Обновления заблокированы: осознанная настройка, а не проблема.
        if (snapshot.UpdateBlocked == true)
        {
            result.Add(new Recommendation(
                "updates.blocked",
                "updates",
                RecommendationSeverity.Info,
                L.T("Обновления Windows заблокированы"),
                L.T("Служба обновлений переведена в «Отключена». Это может быть осознанной настройкой: обновления не устанавливаются, пока блокировка не снята."),
                L.T("Открыть обновления"),
                16));
        }

        // Обновления приостановлены: осознанная настройка, а не проблема.
        if (snapshot.UpdatePaused == true)
        {
            result.Add(new Recommendation(
                "updates.paused",
                "updates",
                RecommendationSeverity.Info,
                L.T("Обновления приостановлены"),
                L.T("Пауза обновлений активна: до конца срока обновления не устанавливаются. Это может быть осознанной настройкой."),
                L.T("Открыть обновления"),
                16));
        }

        // Области, которые не удалось прочитать: честно показываем, что именно.
        if (snapshot.AreaErrors.Count > 0)
        {
            result.Add(new Recommendation(
                "scan.area_errors",
                "scan",
                RecommendationSeverity.Warning,
                L.T("Не удалось прочитать часть областей"),
                L.T("Области: {0}. Данные этих областей в снимке отсутствуют.", string.Join(", ", snapshot.AreaErrors.Keys)),
                L.T("Повторить сканирование"),
                null));
        }

        return result;
    }

    // Системный диск = диск с корнем каталога Windows. Не найден — правила диска молчат.
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
