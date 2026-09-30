using Microsoft.Win32;
using SCU.Common;

namespace SCU.Infrastructure.Windows.Tweaks;

// Раздел 13 «UAC» — аналог :SecMenu из Utilities.bat.
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

    // true = UAC на стандартном уровне; отсутствующий параметр = стандарт Windows.
    // Сбой чтения (отказ в доступе, сломанный WMI/реестр) бросает исключение, а не
    // подменяется стандартным значением: иначе бенчмарк считает метрику UAC по
    // выдуманным данным и сбой не попадает в AreaErrors.
    public bool IsStandard()
    {
        var psd = ReadDword("PromptOnSecureDesktop", missingMeans: 1);
        var cpba = ReadDword("ConsentPromptBehaviorAdmin", missingMeans: 5);
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

        bool actual;
        try
        {
            actual = IsStandard();
        }
        catch (Exception exception)
        {
            return Result.Failure("UAC: не удалось проверить состояние после записи — " + exception.Message);
        }

        return actual == standard
            ? Result.Success(standard
                ? "UAC включён на стандартном уровне (проверено чтением)."
                : "UAC ослаблен (проверено чтением). Требуется перезагрузка для полного применения.")
            : Result.Failure($"UAC: состояние не подтвердилось чтением (фактически {(actual ? "стандарт" : "ослаблен")}).");
    }

    // Отсутствующий параметр → missingMeans (стандарт Windows); ошибка чтения — исключение.
    private static int ReadDword(string name, int missingMeans)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
        using var key = baseKey.OpenSubKey(SystemPolicies);
        if (key is null)
        {
            throw new InvalidOperationException($"Раздел {SystemPolicies} не читается.");
        }

        var raw = key.GetValue(name);
        return raw is null ? missingMeans : raw is int intValue ? intValue : Convert.ToInt32(raw);
    }
}
