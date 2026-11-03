using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;

namespace SCU.Infrastructure.Windows.Tweaks;

// Карточка «Защитник Windows» (подвкладка «Windows Defender» группы
// «Приватность и защита»): выключение/включение Защитника на уровне системы
// — SCU.ps1 action 'defenderdisable' / 'defenderenable'. Фактическое состояние
// читается из реестра: политика DisableAntiSpyware и Start службы WinDefend
// (то, что переустанавливает скрипт).
public sealed class DefenderService
{
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender";
    private const string MainServiceKey = @"SYSTEM\CurrentControlSet\Services\WinDefend";

    // Бэкап стартовых значений служб защитника: пишет скрипт (OutFile),
    // подставляет обратно при включении (InFile).
    private static string BackupFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SCU", "backup", "defender", "services.txt");

    // Защитник выключен, если отключена политика ИЛИ служба WinDefend переведена
    // в «Отключена» (Start=4) — совпадает с тем, что переустанавливает
    // defenderdisable. Ошибка чтения → включённое состояние: тумблер не должен
    // предлагать повторное выключение по сбою чтения реестра.
    public static bool IsDisabled()
    {
        try
        {
            using (var policy = Registry.LocalMachine.OpenSubKey(PolicyKey))
            {
                if (policy?.GetValue("DisableAntiSpyware") is int policyValue && policyValue == 1)
                {
                    return true;
                }
            }

            using (var service = Registry.LocalMachine.OpenSubKey(MainServiceKey))
            {
                return service?.GetValue("Start") is int startValue && startValue == 4;
            }
        }
        catch
        {
            return false;
        }
    }

    public async Task<Result> DisableAsync(SCURunner runner, CancellationToken ct = default) =>
        await runner.RunAsync(
            "defenderdisable",
            new Dictionary<string, string?> { ["OutFile"] = BackupFile },
            null,
            ct).ConfigureAwait(false);

    public async Task<Result> EnableAsync(SCURunner runner, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(
            "defenderenable",
            new Dictionary<string, string?> { ["InFile"] = BackupFile },
            null,
            ct).ConfigureAwait(false);

        // Полностью успешное включение — бэкап больше не означает «выключено»:
        // он удаляется (образец — RestoreCeipTasksAsync в PrivacyService).
        if (result.IsSuccess && File.Exists(BackupFile))
        {
            try
            {
                File.Delete(BackupFile);
            }
            catch
            {
                // Не критично: повторное включение перезапишет файл бэкапа.
            }
        }

        return result;
    }
}
