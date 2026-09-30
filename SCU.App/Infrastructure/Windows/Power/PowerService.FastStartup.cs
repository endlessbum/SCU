using System.Globalization;
using System.Management;
using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.Infrastructure.Windows.Power;

// Питание: гибернация, быстрый запуск, состояние схем (п. 13 аудита: зона ответственности).
public sealed partial class PowerService
{
    private static IReadOnlyList<RegistryTweak> FastBootTweak =>
    [
        new RegistryTweak(
            RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Power",
            "HiberbootEnabled",
            RegistryValueKind.DWord,
            0, 1)
    ];

    // Активация плана: канонические схемы (Balanced и т.п.) матчатся по GUID напрямую.
    // Для шаблонов Ultimate/Bitsum, которых нет в /list, используется стабильный
    // destination GUID. Это убирает зависимость от случайного GUID и от plans.json.
    public async Task<Result> SetPowerPlanAsync(string templateGuid, CancellationToken ct = default)
    {
        var list = await _runner.RunAsync("powercfg", ["/list"], null, ct).ConfigureAwait(false);
        if (!list.IsSuccess)
        {
            return Result.Failure("Не удалось прочитать список планов: " + list.Message, list.Code);
        }

        var guids = LongProcessRunner.ExtractGuids(list.Value ?? string.Empty);
        var activeGuid = guids.FirstOrDefault(g =>
            string.Equals(g, templateGuid, StringComparison.OrdinalIgnoreCase));

        if (activeGuid is null)
        {
            var stableDestination = GetStableDuplicateGuid(templateGuid);

            // Поддержка схем, созданных старой версией приложения: если старый
            // случайный GUID ещё жив, используем его вместо дополнительного клона.
            var plans = LoadDuplicatePlans();
            if (plans.TryGetValue(templateGuid, out var remembered)
                && guids.Any(g => string.Equals(g, remembered, StringComparison.OrdinalIgnoreCase)))
            {
                activeGuid = remembered;
            }
            else if (stableDestination is not null
                && guids.Any(g => string.Equals(g, stableDestination, StringComparison.OrdinalIgnoreCase)))
            {
                activeGuid = stableDestination;
            }
            else
            {
                var duplicateArgs = stableDestination is null
                    ? new[] { "-duplicatescheme", templateGuid }
                    : new[] { "-duplicatescheme", templateGuid, stableDestination };

                var duplicate = await _runner
                    .RunAsync("powercfg", duplicateArgs, null, ct)
                    .ConfigureAwait(false);

                if (!duplicate.IsSuccess)
                {
                    // Безопасно переживаем гонку с другим экземпляром приложения:
                    // если схема успела появиться, переиспользуем её, а не клонируем ещё раз.
                    var retryList = await _runner.RunAsync("powercfg", ["/list"], null, ct)
                        .ConfigureAwait(false);
                    var retryGuids = retryList.IsSuccess
                        ? LongProcessRunner.ExtractGuids(retryList.Value ?? string.Empty)
                        : [];
                    var duplicateGuid = stableDestination ?? LongProcessRunner.ExtractGuids(duplicate.Value ?? string.Empty).LastOrDefault();
                    activeGuid = duplicateGuid is not null && retryGuids.Any(g =>
                        string.Equals(g, duplicateGuid, StringComparison.OrdinalIgnoreCase))
                        ? duplicateGuid
                        : null;

                    if (activeGuid is null)
                    {
                        return Result.Failure("Не удалось создать копию плана: " + duplicate.Message, duplicate.Code);
                    }
                }
                else
                {
                    activeGuid = stableDestination
                        ?? LongProcessRunner.ExtractGuids(duplicate.Value ?? string.Empty).LastOrDefault();
                    if (activeGuid is null)
                    {
                        return Result.Failure("powercfg не вернул GUID нового плана.");
                    }

                    if (stableDestination is not null)
                    {
                        plans[templateGuid] = stableDestination;
                        SaveDuplicatePlans(plans);
                    }
                }
            }
        }

        var activate = await _runner
            .RunAsync("powercfg", ["/setactive", activeGuid], null, ct)
            .ConfigureAwait(false);
        if (!activate.IsSuccess)
        {
            return Result.Failure(activate.Message, activate.Code);
        }

        var verify = await _runner
            .RunAsync("powercfg", ["/getactivescheme"], null, ct)
            .ConfigureAwait(false);
        if (!verify.IsSuccess)
        {
            return Result.Failure("План активирован, но не удалось подтвердить его чтением: " + verify.Message, verify.Code);
        }

        var verifiedGuid = LongProcessRunner.ExtractGuids(verify.Value ?? string.Empty).FirstOrDefault();
        if (!string.Equals(verifiedGuid, activeGuid, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure($"Активная схема не совпала: фактически {verifiedGuid ?? "не определена"}, ожидалось {activeGuid}.");
        }

        _logger.Info($"POWER | plan {activeGuid} activated and verified");
        return Result.Success("План электропитания установлен и подтверждён.");
    }

    public async Task<Result> SetHibernationAsync(bool enable, CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync("powercfg", ["/h", enable ? "on" : "off"], null, ct)
            .ConfigureAwait(false);
        _logger.Info($"POWER | hibernation={enable} | rc={result.Code}");
        return result.IsSuccess
            ? Result.Success(enable ? "Гибернация включена." : "Гибернация отключена.")
            : Result.Failure("powercfg завершился с кодом " + result.Code, result.Code);
    }

    public Result SetFastBoot(bool disable)
    {
        var tweak = FastBootTweak[0] with { OffValue = disable ? 0 : 1 };
        var result = _registry.Apply([tweak], BackupPath("fastboot.json"));
        if (!result.IsSuccess)
            return result;

        // Подтверждение записью: HiberbootEnabled должен совпасть с ожидаемым.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            var actual = key?.GetValue("HiberbootEnabled") as int?;
            var expected = disable ? 0 : 1;
            if (actual != expected)
            {
                return Result.Failure(
                    "HiberbootEnabled записан, но повторное чтение не подтвердило значение. " +
                    "Проверьте права администратора.");
            }
        }
        catch (Exception ex)
        {
            return Result.Failure("Не удалось подтвердить HiberbootEnabled после записи: " + ex.Message);
        }

        // Полное применение hybrid shutdown — после перезагрузки; вызывающий UI ставит PendingReboot.
        return Result.Success(disable
            ? "Быстрый запуск отключён (требуется перезагрузка)."
            : "Быстрый запуск включён (требуется перезагрузка).");
    }

    /// <summary>
    /// Совместимость: true, если HiberbootEnabled == 0.
    /// Предпочтительно использовать <see cref="GetFastStartupStateAsync"/>.
    /// </summary>
    public static bool IsFastBootDisabled(RegistryHelper registry) => registry.IsApplied(FastBootTweak);

    /// <summary>
    /// Фактическое состояние Fast Startup.
    /// Учитывает HiberbootEnabled, наличие/тип hiberfile и доступность гибридного сна (powercfg /a).
    /// Не сводит ошибку чтения к false.
    /// </summary>
    public async Task<Result<FastStartupInfo>> GetFastStartupStateAsync(CancellationToken ct = default)
    {
        try
        {
            int? hiberboot = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
                hiberboot = key?.GetValue("HiberbootEnabled") as int?;
            }
            catch (UnauthorizedAccessException)
            {
                return Result<FastStartupInfo>.Failure("Нет доступа к реестру HiberbootEnabled.", 5);
            }
            catch (Exception ex)
            {
                return Result<FastStartupInfo>.Failure("Не удалось прочитать HiberbootEnabled: " + ex.Message);
            }

            // powercfg /a — какие состояния сна реально доступны.
            var available = await _runner
                .RunAsync("powercfg", ["/a"], null, ct)
                .ConfigureAwait(false);

            var hybridAvailable = false;
            var hibernateAvailable = false;
            if (available.IsSuccess && available.Value is not null)
            {
                var text = available.Value;
                // Англ. и локализованные формулировки.
                hybridAvailable = text.Contains("Hybrid Sleep", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Гибридный спящий", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Fast Startup", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Быстрый запуск", StringComparison.OrdinalIgnoreCase);
                hibernateAvailable = text.Contains("Hibernate", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Гибернац", StringComparison.OrdinalIgnoreCase);
            }

            // Тип hiberfile: full / reduced / none (через размер файла и HibernateEnabledDefault / HiberFileSizePercent).
            var hiberFileType = DetectHiberfileType();

            // Pending reboot: реестр уже другой, а runtime (hiberfile / powercfg) ещё старый.
            var requiresReboot = false;
            SystemSettingState state;
            if (hiberboot is null && !available.IsSuccess)
            {
                state = SystemSettingState.Unknown;
            }
            else if (hiberboot is 1 && hiberFileType == HiberfileType.None)
            {
                // Записано «включено», файла нет — либо недоступно, либо ждёт применения после reboot.
                // Если hibernate в powercfg тоже недоступна, скорее Unavailable; иначе PendingReboot.
                if (!hibernateAvailable && available.IsSuccess)
                {
                    state = SystemSettingState.Unavailable;
                }
                else
                {
                    state = SystemSettingState.PendingReboot;
                    requiresReboot = true;
                }
            }
            else if (hiberboot is 0 && hiberFileType is HiberfileType.Reduced or HiberfileType.Full)
            {
                // Реестр «выкл», но hiberfile ещё на месте — полное применение после reboot.
                state = SystemSettingState.PendingReboot;
                requiresReboot = true;
            }
            else if (hiberboot is 0)
            {
                state = SystemSettingState.Disabled;
            }
            else if (hiberboot is 1 || hiberboot is null)
            {
                // null = значение по умолчанию Windows (обычно включено на клиентских SKU).
                state = SystemSettingState.Enabled;
            }
            else
            {
                state = SystemSettingState.Unknown;
            }

            return Result<FastStartupInfo>.Success(new FastStartupInfo(
                state,
                hiberboot,
                hiberFileType,
                hybridAvailable,
                hibernateAvailable,
                RequiresReboot: requiresReboot));
        }
        catch (OperationCanceledException)
        {
            return Result<FastStartupInfo>.Failure("Отменено", -1);
        }
        catch (Exception ex)
        {
            return Result<FastStartupInfo>.Failure(ex.Message);
        }
    }

    private static HiberfileType DetectHiberfileType()
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var hiberPath = Path.Combine(systemDrive, "hiberfil.sys");
            if (!File.Exists(hiberPath))
            {
                // Файл может быть скрыт/system — пробуем атрибуты через Directory.
                try
                {
                    var info = new FileInfo(hiberPath);
                    if (!info.Exists)
                        return HiberfileType.None;
                }
                catch
                {
                    return HiberfileType.None;
                }
            }

            // HiberFileType в реестре (Windows 10+): 0 = full, 1 = reduced (Fast Startup only).
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            var type = key?.GetValue("HiberFileType") as int?;
            return type switch
            {
                0 => HiberfileType.Full,
                1 => HiberfileType.Reduced,
                _ => HiberfileType.Unknown
            };
        }
        catch
        {
            return HiberfileType.Unknown;
        }
    }

    /// <summary>
    /// Только чтение: включена ли гибернация.
    /// Ошибка доступа → не «включено по умолчанию», а отдельный сбой (вызывающий код обрабатывает).
    /// </summary>
    public static bool? TryIsHibernationEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            return key?.GetValue("HibernateEnabled") switch
            {
                int intValue => intValue != 0,
                null => true, // отсутствие ключа = поведение по умолчанию Windows
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Совместимость: при ошибке чтения возвращает true (старое поведение). Предпочтительно TryIsHibernationEnabled.</summary>
    public static bool IsHibernationEnabled() => TryIsHibernationEnabled() ?? true;
}
