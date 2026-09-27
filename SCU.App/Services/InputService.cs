using System.Runtime.InteropServices;
using Microsoft.Win32;
using SCU.Common;

namespace SCU.Services;

// Один переключатель раздела Input — та же модель, что UiSwitchOption.
public sealed record InputSwitchOption(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<RegistryTweak> OnTweaks,
    IReadOnlyList<RegistryTweak> OffTweaks,
    Func<bool>? ReadState = null,
    Func<bool, Result>? ApplyState = null);

// Раздел 11 «Ввод, браузер и игры» — аналог :InpMenu из Utilities.bat:
// ускорение мыши (реестр + SystemParametersInfo), залипание клавиш, ускорение Edge,
// Game Bar / DVR / Game Mode. Изменения — через RegistryHelper (backup → change → verify).
public sealed class InputService
{
    private const string Mouse = @"Control Panel\Mouse";
    private const string StickyKeys = @"Control Panel\Accessibility\StickyKeys";
    private const string KeyboardResponse = @"Control Panel\Accessibility\Keyboard Response";
    private const string ToggleKeys = @"Control Panel\Accessibility\ToggleKeys";
    private const string EdgePolicies = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string GameDvr = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameConfigStore = @"System\GameConfigStore";
    private const string GameBar = @"SOFTWARE\Microsoft\GameBar";
    private const string GameDvrPolicies = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";

    // SystemParametersInfo(SPI_SETMOUSE) — применение ускорения мыши без выхода из сеанса (как в BAT).
    private const uint SpiSetMouse = 0x0004;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] buffer, uint iniFlags);

    private readonly Logger _logger;
    private readonly RegistryHelper _registry;
    private readonly string _backupDirectory;

    public InputService(Logger logger, RegistryHelper registry)
    {
        _logger = logger;
        _registry = registry;
        _backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU", "backup", "input");

        Switches = BuildSwitches();
    }

    public IReadOnlyList<InputSwitchOption> Switches { get; }

    private InputSwitchOption[] BuildSwitches()
    {
        return
        [
            new InputSwitchOption(
                "mouse-acceleration",
                "Ускорение мыши",
                "Отключение даёт предсказуемую чувствительность — часто удобнее для игр. Для обычной работы включённое ускорение мыши может быть комфортнее.",
                [
                    SZ(RegistryHive.CurrentUser, Mouse, "MouseSpeed", "1"),
                    SZ(RegistryHive.CurrentUser, Mouse, "MouseThreshold1", "6"),
                    SZ(RegistryHive.CurrentUser, Mouse, "MouseThreshold2", "10")
                ],
                [
                    SZ(RegistryHive.CurrentUser, Mouse, "MouseSpeed", "0"),
                    SZ(RegistryHive.CurrentUser, Mouse, "MouseThreshold1", "0"),
                    SZ(RegistryHive.CurrentUser, Mouse, "MouseThreshold2", "0")
                ],
                () => ReadString(RegistryHive.CurrentUser, Mouse, "MouseSpeed", "1") != "0",
                on => ApplyMouse(on)),

            new InputSwitchOption(
                "sticky-keys",
                "Залипание клавиш",
                "Также отключает фильтрацию ввода и озвучивание переключений. Обычно оставляют выключенным, если специальные возможности залипания клавиш не нужны.",
                [
                    SZ(RegistryHive.CurrentUser, StickyKeys, "Flags", "510"),
                    SZ(RegistryHive.CurrentUser, KeyboardResponse, "Flags", "126"),
                    SZ(RegistryHive.CurrentUser, ToggleKeys, "Flags", "62")
                ],
                [
                    SZ(RegistryHive.CurrentUser, StickyKeys, "Flags", "506"),
                    SZ(RegistryHive.CurrentUser, KeyboardResponse, "Flags", "122"),
                    SZ(RegistryHive.CurrentUser, ToggleKeys, "Flags", "58")
                ],
                () => ReadString(RegistryHive.CurrentUser, StickyKeys, "Flags", "510") != "506"),

            new InputSwitchOption(
                "edge-startup-boost",
                "Ускорение запуска Edge",
                "Startup Boost и фоновая работа Edge после закрытия окон. Обычно включают на ПК, где Edge — основной браузер; отключение уменьшает фоновую активность.",
                [],
                [
                    T(RegistryHive.LocalMachine, EdgePolicies, "StartupBoostEnabled", 0),
                    T(RegistryHive.LocalMachine, EdgePolicies, "BackgroundModeEnabled", 0)
                ],
                () => ReadDword(RegistryHive.LocalMachine, EdgePolicies, "StartupBoostEnabled", 0) != 0
                    || ReadDword(RegistryHive.LocalMachine, EdgePolicies, "BackgroundModeEnabled", 0) != 0,
                on => ApplyEdgeBoost(on)),

            new InputSwitchOption(
                "game-bar",
                "Game Bar",
                "Игровая панель и оверлей по Win+G. Включайте, если пользуетесь записью/оверлеем Xbox Game Bar; иначе можно отключить.",
                [
                    T(RegistryHive.CurrentUser, GameDvr, "AppCaptureEnabled", 1),
                    T(RegistryHive.CurrentUser, GameConfigStore, "GameDVR_Enabled", 1),
                    T(RegistryHive.CurrentUser, GameBar, "UseNexusForGameBarEnabled", 1),
                    T(RegistryHive.LocalMachine, GameDvrPolicies, "AllowGameDVR", 1)
                ],
                [
                    T(RegistryHive.CurrentUser, GameDvr, "AppCaptureEnabled", 0),
                    T(RegistryHive.CurrentUser, GameConfigStore, "GameDVR_Enabled", 0),
                    T(RegistryHive.CurrentUser, GameBar, "UseNexusForGameBarEnabled", 0),
                    T(RegistryHive.LocalMachine, GameDvrPolicies, "AllowGameDVR", 0)
                ],
                () => ReadDword(RegistryHive.CurrentUser, GameConfigStore, "GameDVR_Enabled", 1) == 1),

            new InputSwitchOption(
                "game-dvr",
                "Фоновая запись и DVR",
                "Запись геймплея в фоне; может влиять на FPS и потребление диска. Обычно держат выключенной, если фоновая запись не нужна.",
                [
                    T(RegistryHive.CurrentUser, GameDvr, "AppCaptureEnabled", 1),
                    T(RegistryHive.CurrentUser, GameDvr, "HistoricalCaptureEnabled", 1),
                    T(RegistryHive.CurrentUser, GameConfigStore, "GameDVR_FSEBehaviorMode", 2),
                    T(RegistryHive.CurrentUser, GameConfigStore, "GameDVR_HonorUserFSEBehaviorMode", 1),
                    T(RegistryHive.CurrentUser, GameConfigStore, "GameDVR_DXGIHonorFSEWindowsCompatible", 1)
                ],
                [
                    T(RegistryHive.CurrentUser, GameDvr, "AppCaptureEnabled", 0),
                    T(RegistryHive.CurrentUser, GameDvr, "HistoricalCaptureEnabled", 0),
                    T(RegistryHive.CurrentUser, GameConfigStore, "GameDVR_FSEBehaviorMode", 2),
                    T(RegistryHive.CurrentUser, GameConfigStore, "GameDVR_HonorUserFSEBehaviorMode", 1),
                    T(RegistryHive.CurrentUser, GameConfigStore, "GameDVR_DXGIHonorFSEWindowsCompatible", 1)
                ],
                () => ReadDword(RegistryHive.CurrentUser, GameDvr, "AppCaptureEnabled", 1) == 1),

            new InputSwitchOption(
                "game-mode",
                "Game Mode",
                "Приоритет ресурсов для игр. Для игрового ПК обычно оставляют включённым; отключение имеет смысл при диагностике или специфических сценариях.",
                [
                    T(RegistryHive.CurrentUser, GameBar, "AutoGameModeEnabled", 1),
                    T(RegistryHive.CurrentUser, GameBar, "AllowAutoGameMode", 1)
                ],
                [
                    T(RegistryHive.CurrentUser, GameBar, "AutoGameModeEnabled", 0),
                    T(RegistryHive.CurrentUser, GameBar, "AllowAutoGameMode", 0)
                ],
                () => ReadDword(RegistryHive.CurrentUser, GameBar, "AutoGameModeEnabled", 1) == 1)
        ];
    }

    public InputSwitchOption? Find(string id) => Switches.FirstOrDefault(s => s.Id == id);

    public bool IsSwitchOn(InputSwitchOption option) =>
        option.ReadState?.Invoke() ?? _registry.IsApplied(option.OnTweaks);

    public Result SetSwitch(InputSwitchOption option, bool on)
    {
        if (option.ApplyState is not null)
        {
            return option.ApplyState(on);
        }

        var tweaks = on ? option.OnTweaks : option.OffTweaks;
        var result = _registry.Apply(tweaks, BackupPath(option.Id + ".json"));
        if (!result.IsSuccess)
        {
            return result;
        }

        var actual = IsSwitchOn(option);
        return actual == on
            ? Result.Success(option.Title + (on ? ": включено. " : ": выключено. ") + result.Message)
            : Result.Failure($"{option.Title}: состояние не подтвердилось чтением.");
    }

    // Пресет «Отключить Game Bar и DVR» из BAT — Game Mode остаётся включённым.
    public Result DisableGameBarAndDvr()
    {
        var bar = SetSwitch(Find("game-bar")!, on: false);
        var dvr = SetSwitch(Find("game-dvr")!, on: false);
        return bar.IsSuccess && dvr.IsSuccess
            ? Result.Success("Game Bar и фоновая запись отключены. Game Mode оставлен включённым.")
            : Result.Failure("Не все параметры Game Bar / DVR применены: " + string.Join(" | ",
                new[] { bar, dvr }.Where(r => !r.IsSuccess).Select(r => r.Message)));
    }

    private Result ApplyMouse(bool enable)
    {
        var speed = enable ? "1" : "0";
        var result = _registry.Apply(
        [
            SZ(RegistryHive.CurrentUser, Mouse, "MouseSpeed", speed),
            SZ(RegistryHive.CurrentUser, Mouse, "MouseThreshold1", enable ? "6" : "0"),
            SZ(RegistryHive.CurrentUser, Mouse, "MouseThreshold2", enable ? "10" : "0")
        ], BackupPath("mouse-acceleration.json"));
        if (!result.IsSuccess)
        {
            return result;
        }

        // SPI_SETMOUSE: мгновенное применение без перелогина.
        var applied = SystemParametersInfo(
            SpiSetMouse, 0,
            enable ? [6, 10, 1] : [0, 0, 0],
            SpifUpdateIniFile | SpifSendChange);
        _logger.Info("INPUT | mouse SPI | rc=" + (applied ? 0 : 1));
        if (!applied)
        {
            return Result.Failure("Реестр обновлён, но SystemParametersInfo не применил параметры.");
        }

        return Result.Success(enable ? "Ускорение мыши включено." : "Ускорение мыши отключено.");
    }

    // Ускорение Edge включено = policy-значения отсутствуют; выключено = записаны нули (как в BAT).
    private Result ApplyEdgeBoost(bool enable)
    {
        if (!enable)
        {
            return _registry.Apply(
            [
                T(RegistryHive.LocalMachine, EdgePolicies, "StartupBoostEnabled", 0),
                T(RegistryHive.LocalMachine, EdgePolicies, "BackgroundModeEnabled", 0)
            ], BackupPath("edge-startup-boost.json"));
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var key = baseKey.OpenSubKey(EdgePolicies, writable: true);
            key?.DeleteValue("StartupBoostEnabled", throwOnMissingValue: false);
            key?.DeleteValue("BackgroundModeEnabled", throwOnMissingValue: false);
        }
        catch (Exception exception)
        {
            return Result.Failure("Edge: " + exception.Message);
        }

        return Result.Success("Ускорение запуска Edge возвращено к поведению по умолчанию.");
    }

    private static RegistryTweak T(RegistryHive hive, string subKey, string name, object value) =>
        new(hive, subKey, name, RegistryValueKind.DWord, value, null);

    private static RegistryTweak SZ(RegistryHive hive, string subKey, string name, string value) =>
        new(hive, subKey, name, RegistryValueKind.String, value, null);

    private static int ReadDword(RegistryHive hive, string subKey, string name, int fallback)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey);
            var raw = key?.GetValue(name);
            return raw is int intValue ? intValue : Convert.ToInt32(raw ?? fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private static string ReadString(RegistryHive hive, string subKey, string name, string fallback)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name) as string ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private string BackupPath(string fileName) => Path.Combine(_backupDirectory, fileName);
}
