using Microsoft.Win32;
using SCU.Common;

namespace SCU.Services;

// Раздел 13 «Безопасность (UAC)» — аналог :SecMenu из Utilities.bat.
// Ослабление: PromptOnSecureDesktop=0 + ConsentPromptBehaviorAdmin=0 (необратимо в рамках утилиты
// считается опасным — требует подтверждения). Стандарт: 1 + 5 (значения Windows по умолчанию).
public sealed class SecurityService
{
    private const string SystemPolicies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private readonly Logger _logger;
    private readonly RegistryHelper _registry;
    private readonly string _backupDirectory;

    public SecurityService(Logger logger, RegistryHelper registry)
    {
        _logger = logger;
        _registry = registry;
        _backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU", "backup", "security");
    }

    public static readonly RegistryTweak[] WeakTweaks =
    [
        new(RegistryHive.LocalMachine, SystemPolicies, "PromptOnSecureDesktop", RegistryValueKind.DWord, 0, null),
        new(RegistryHive.LocalMachine, SystemPolicies, "ConsentPromptBehaviorAdmin", RegistryValueKind.DWord, 0, null)
    ];

    public static readonly RegistryTweak[] StandardTweaks =
    [
        new(RegistryHive.LocalMachine, SystemPolicies, "PromptOnSecureDesktop", RegistryValueKind.DWord, 1, null),
        new(RegistryHive.LocalMachine, SystemPolicies, "ConsentPromptBehaviorAdmin", RegistryValueKind.DWord, 5, null)
    ];

    // true = UAC на стандартном уровне; значения отсутствуют считаются стандартом Windows.
    public bool IsStandard()
    {
        var psd = ReadDword("PromptOnSecureDesktop", 1);
        var cpba = ReadDword("ConsentPromptBehaviorAdmin", 5);
        return psd == 1 && cpba == 5;
    }

    public Result SetStandard(bool standard)
    {
        var tweaks = standard ? StandardTweaks : WeakTweaks;
        var result = _registry.Apply(tweaks, Path.Combine(_backupDirectory, "uac.json"));
        if (!result.IsSuccess)
        {
            return result;
        }

        var actual = IsStandard();
        return actual == standard
            ? Result.Success(standard
                ? "UAC включён на стандартном уровне (проверено чтением)."
                : "UAC ослаблен (проверено чтением). Требуется перезагрузка для полного применения.")
            : Result.Failure($"UAC: состояние не подтвердилось чтением (фактически {(actual ? "стандарт" : "ослаблен")}).");
    }

    private static int ReadDword(string name, int fallback)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var key = baseKey.OpenSubKey(SystemPolicies);
            var raw = key?.GetValue(name);
            return raw is int intValue ? intValue : Convert.ToInt32(raw ?? fallback);
        }
        catch
        {
            return fallback;
        }
    }
}
