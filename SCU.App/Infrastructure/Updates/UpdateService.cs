using System.Globalization;
using Microsoft.Win32;
using SCU.Common;

namespace SCU.Infrastructure.Updates;

// Раздел 16 «Обновления Windows»: пауза обновлений и запрет авто-драйверов через реестр
// (те же величины, что пишет приложение «Параметры»). Блокировка/возврат служб
// оркестрирует UpdateViewModel по паттерну CleanupViewModel: ServicesBackup -> disable -> ServicesRestore.
public sealed class UpdateService
{
    public static IReadOnlyList<string> UpdateServiceNames { get; } =
    [
        "wuauserv",
        "UsoSvc",
        "WaaSMedicSvc",
        "DoSvc",
        "BITS"
    ];

    public static string UpdateServicesCsv => string.Join(",", UpdateServiceNames);

    private const string UxSettingsSubKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string WindowsUpdateSubKey = @"SOFTWARE\Microsoft\WindowsUpdate";
    private const string PauseDateFormat = "yyyy-MM-ddTHH:mm:ssZ";

    private static readonly string[] PauseValueNames =
    [
        "PauseUpdatesStartTime",
        "PauseUpdatesExpiryTime",
        "PauseFeatureUpdatesStartTime",
        "PauseFeatureUpdatesEndTime",
        "PauseQualityUpdatesStartTime",
        "PauseQualityUpdatesEndTime"
    ];

    private readonly Logger _logger;
    private readonly RegistryHelper _registry;

    public UpdateService(Logger logger, RegistryHelper registry)
    {
        _logger = logger;
        _registry = registry;
    }

    // Величины паузы пишутся с датами; Restore/ResetToDefault нужен только список имён.
    private static IReadOnlyList<RegistryTweak> BuildPauseTweaks(string startIso, string endIso)
    {
        var tweaks = new List<RegistryTweak>(PauseValueNames.Length);
        foreach (var name in PauseValueNames)
        {
            var value = name.EndsWith("StartTime", StringComparison.Ordinal) ? startIso : endIso;
            tweaks.Add(new RegistryTweak(
                RegistryHive.LocalMachine,
                UxSettingsSubKey,
                name,
                RegistryValueKind.String,
                value,
                null));
        }

        return tweaks;
    }

    private static IReadOnlyList<RegistryTweak> BuildPauseTweaks() => BuildPauseTweaks(string.Empty, string.Empty);

    private static IReadOnlyList<RegistryTweak> DriverExclusionTweak =>
    [
        new RegistryTweak(
            RegistryHive.LocalMachine,
            WindowsUpdateSubKey,
            "ExcludeWUDriversInQualityUpdate",
            RegistryValueKind.DWord,
            1, 0)
    ];

    public Result PauseUpdates(int days)
    {
        // Windows ограничивает паузу ~35 днями; вне диапазона результат непредсказуем.
        if (days is < 1 or > 35)
        {
            return Result.Failure("Пауза обновлений должна быть от 1 до 35 дней.");
        }

        var start = DateTime.UtcNow.ToString(PauseDateFormat, CultureInfo.InvariantCulture);
        var end = DateTime.UtcNow.AddDays(days).ToString(PauseDateFormat, CultureInfo.InvariantCulture);
        var result = _registry.Apply(BuildPauseTweaks(start, end), BackupPath("wupause.json"));
        if (result.IsSuccess)
        {
            _logger.Info($"WU | pause | days={days}");
        }

        return result;
    }

    // «Снять паузу»: исходные значения из резерва (отсутствовавшие — удаляются);
    // резерва нет (пауза ставилась не из приложения) — значения просто удаляются.
    public Result UnpauseUpdates()
    {
        var backupFile = BackupPath("wupause.json");
        Result result;

        if (File.Exists(backupFile))
        {
            // При наличии backup восстанавливаем именно исходные значения.
            // Ошибка Restore НЕ превращается в разрушительный ResetToDefault.
            result = _registry.Restore(BuildPauseTweaks(), backupFile);
        }
        else
        {
            // Пауза могла быть установлена не этой программой: в этом случае
            // безопаснее удалить только известные pause-параметры.
            result = _registry.ResetToDefault(BuildPauseTweaks());
        }

        if (result.IsSuccess)
        {
            _logger.Info("WU | pause removed");
            if (File.Exists(backupFile))
            {
                try
                {
                    File.Delete(backupFile);
                }
                catch (Exception exception)
                {
                    _logger.Warn("WU | pause backup cleanup failed: " + exception.Message);
                }
            }
        }

        return result;
    }

    public Result SetDriverUpdatesExcluded(bool exclude)
    {
        var tweak = DriverExclusionTweak[0] with { OffValue = exclude ? 1 : 0 };
        var result = _registry.Apply([tweak], BackupPath("wudrivers.json"));
        if (result.IsSuccess)
        {
            _logger.Info($"WU | driver exclusion={exclude}");
        }

        return result;
    }

    public static bool IsDriverUpdatesExcluded()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WindowsUpdateSubKey);
            return key?.GetValue("ExcludeWUDriversInQualityUpdate") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    // Читает PauseUpdatesExpiryTime и разбирает дату (общий хелпер для IsPaused и текста паузы).
    // null — значения нет или дата не распознана.
    private static DateTime? ReadPauseExpiryUtc()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UxSettingsSubKey);
            var expiry = key?.GetValue("PauseUpdatesExpiryTime") as string;
            if (string.IsNullOrWhiteSpace(expiry))
            {
                return null;
            }

            return DateTime.TryParse(
                expiry,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var moment)
                ? moment
                : null;
        }
        catch
        {
            return null;
        }
    }

    // Пауза активна: срок существует и ещё не наступил.
    public static bool IsPaused()
    {
        var expiry = ReadPauseExpiryUtc();
        return expiry.HasValue && expiry.Value.ToLocalTime() > DateTime.Now;
    }

    // Текст паузы для карточки: читаем срок из UX\Settings.
    public static string GetPauseInfoText()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UxSettingsSubKey);
            var expiry = key?.GetValue("PauseUpdatesExpiryTime") as string;
            if (string.IsNullOrWhiteSpace(expiry))
            {
                return "Пауза не задана — Windows обновляется по своему расписанию.";
            }

            var moment = ReadPauseExpiryUtc();
            if (moment is null)
            {
                return "Пауза задана (не удалось разобрать срок: " + expiry + ").";
            }

            var local = moment.Value.ToLocalTime();
            return local > DateTime.Now
                ? $"Обновления приостановлены до {local:dd.MM.yyyy HH:mm} (местное время)."
                : "Пауза истекла — Windows снова будет устанавливать обновления.";
        }
        catch (Exception exception)
        {
            return "Не удалось прочитать состояние паузы: " + exception.Message;
        }
    }

    // Обновления заблокированы = служба wuauserv переведена в «Отключена» (Start=4).
    public static bool IsBlocked()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\wuauserv");
            return key?.GetValue("Start") is int start && start == 4;
        }
        catch
        {
            return false;
        }
    }

    private static string BackupPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "update", fileName);
}
