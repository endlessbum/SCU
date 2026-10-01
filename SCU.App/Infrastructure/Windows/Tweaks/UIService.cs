using System.Diagnostics;
using Microsoft.Win32;
using SCU.Common;

namespace SCU.Infrastructure.Windows.Tweaks;

// Один переключатель раздела UI: тумблер «вкл/выкл».
// OnTweaks/OffTweaks — значения реестра для обоих состояний; для особых случаев
// (удаление policy-значений, ключ CLSID классического меню) задаются ReadState/ApplyState.
public sealed record UiSwitchOption(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<RegistryTweak> OnTweaks,
    IReadOnlyList<RegistryTweak> OffTweaks,
    Func<bool>? ReadState = null,
    Func<bool, Result>? ApplyState = null);

// Раздел 10 «Интерфейс и проводник» — аналог :UIMenu из Utilities.bat.
// Все изменения — через RegistryHelper (backup → change → verify).
public sealed class UIService
{
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Exp = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string Clsid = @"Software\Classes\CLSID";
    private const string ClsidHome = Clsid + @"\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    private const string ClsidGallery = Clsid + @"\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
    private const string ClsidNetwork = Clsid + @"\{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
    private const string ClsidRecycle = Clsid + @"\{645FF040-5081-101B-9F08-00AA002F954E}";
    private const string ClsidClassicMenu = Clsid + @"\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string ClsidOneDrive = Clsid + @"\{018D5C66-4533-4307-9B53-224DE2ED1FE6}";
    private const string DeskIcons = Exp + @"\HideDesktopIcons\NewStartPanel";
    private const string Desktop = @"Control Panel\Desktop";
    private const string WindowMetrics = @"Control Panel\Desktop\WindowMetrics";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string VisualEffects = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    private const string PoliciesExplorer = @"SOFTWARE\Policies\Microsoft\Windows\Explorer";
    private const string PolicyManagerStart = @"SOFTWARE\Microsoft\PolicyManager\current\device\Start";
    private const string PolicyManagerEducation = @"SOFTWARE\Microsoft\PolicyManager\current\device\Education";

    private const string PinValue = "System.IsPinnedToNameSpaceTree";

    private readonly Logger _logger;
    private readonly RegistryHelper _registry;
    private readonly string _backupDirectory;

    public UIService(Logger logger, RegistryHelper registry)
    {
        _logger = logger;
        _registry = registry;
        _backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU", "backup", "ui");

        ExplorerSwitches = BuildExplorerSwitches();
        VisualFxSwitches = BuildVisualFxSwitches();
    }

    public IReadOnlyList<UiSwitchOption> ExplorerSwitches { get; }

    public IReadOnlyList<UiSwitchOption> VisualFxSwitches { get; }

    private IReadOnlyList<UiSwitchOption> BuildExplorerSwitches()
    {
        return
        [
            new UiSwitchOption(
                "open-to-this-pc",
                "Проводник открывает «Этот компьютер»",
                "Иначе открывается «Главная» (быстрый доступ). Обычно удобно включать, если вы чаще работаете с дисками и папками, а не с недавними файлами.",
                [T(RegistryHive.CurrentUser, Adv, "LaunchTo", 1)],
                [T(RegistryHive.CurrentUser, Adv, "LaunchTo", 2)],
                () => ReadDword(RegistryHive.CurrentUser, Adv, "LaunchTo", 2) == 1),

            new UiSwitchOption(
                "home-button",
                "Кнопка «Главная» в навигации",
                "Показывает или скрывает «Главную» в дереве проводника. Обычно оставляют включённой; отключайте, если хотите более компактную навигацию.",
                [Pin(ClsidHome, 1)],
                [Pin(ClsidHome, 0)],
                () => ReadDword(RegistryHive.CurrentUser, ClsidHome, PinValue, 0) == 1),

            new UiSwitchOption(
                "gallery-button",
                "Кнопка «Галерея» в навигации",
                "Коллекция фото в дереве проводника. Обычно оставляют включённой только тем, кто пользуется Галереей; иначе можно отключить.",
                [Pin(ClsidGallery, 1)],
                [Pin(ClsidGallery, 0)],
                () => ReadDword(RegistryHive.CurrentUser, ClsidGallery, PinValue, 0) == 1),

            new UiSwitchOption(
                "network-button",
                "Кнопка «Сеть» в навигации",
                "Соседние компьютеры и сетевые устройства. Включайте, если регулярно используете сетевые ресурсы; иначе можно скрыть пункт.",
                [Pin(ClsidNetwork, 1)],
                [Pin(ClsidNetwork, 0)],
                () => ReadDword(RegistryHive.CurrentUser, ClsidNetwork, PinValue, 0) == 1),

            new UiSwitchOption(
                "recycle-nav",
                "Корзина в навигации",
                "Показывает Корзину в левой панели Проводника. Обычно оставляют включённой для быстрого доступа.",
                [Pin(ClsidRecycle, 1)],
                [Pin(ClsidRecycle, 0)],
                () => ReadDword(RegistryHive.CurrentUser, ClsidRecycle, PinValue, 0) == 1),

            new UiSwitchOption(
                "recycle-desktop",
                "Корзина на рабочем столе",
                "Показывает Корзину на рабочем столе. Обычно оставляют включённой; отключайте только для минималистичного рабочего стола.",
                [T(RegistryHive.CurrentUser, DeskIcons, "{645FF040-5081-101B-9F08-00AA002F954E}", 0)],
                [T(RegistryHive.CurrentUser, DeskIcons, "{645FF040-5081-101B-9F08-00AA002F954E}", 1)],
                () => ReadDword(RegistryHive.CurrentUser, DeskIcons, "{645FF040-5081-101B-9F08-00AA002F954E}", 0) == 0),

            new UiSwitchOption(
                "compact-view",
                "Компактный вид проводника",
                "Уменьшает расстояние между файлами. Обычно удобно включить на экране с высокой плотностью элементов; иначе стандартный вид проще читать.",
                [T(RegistryHive.CurrentUser, Adv, "UseCompactMode", 1)],
                [T(RegistryHive.CurrentUser, Adv, "UseCompactMode", 0)]),

            new UiSwitchOption(
                "recent-files",
                "Недавние файлы",
                "Последние файлы и частые папки в быстром доступе. Обычно оставляют включёнными; отключение имеет смысл ради приватности.",
                [
                    T(RegistryHive.CurrentUser, Exp, "ShowRecent", 1),
                    T(RegistryHive.CurrentUser, Exp, "ShowFrequent", 1),
                    T(RegistryHive.CurrentUser, Exp, "ShowCloudFilesInQuickAccess", 1),
                    T(RegistryHive.CurrentUser, Adv, "Start_TrackDocs", 1)
                ],
                [
                    T(RegistryHive.CurrentUser, Exp, "ShowRecent", 0),
                    T(RegistryHive.CurrentUser, Exp, "ShowFrequent", 0),
                    T(RegistryHive.CurrentUser, Exp, "ShowCloudFilesInQuickAccess", 0),
                    T(RegistryHive.CurrentUser, Adv, "Start_TrackDocs", 0)
                ],
                () => ReadDword(RegistryHive.CurrentUser, Exp, "ShowRecent", 1) == 1),

            new UiSwitchOption(
                "classic-context-menu",
                "Классическое контекстное меню",
                "Полное меню Windows 10 вместо свёрнутого Windows 11. Включайте только если нужен классический контекстный список; после изменения перезапустите Проводник.",
                [],
                [],
                () => KeyExists(RegistryHive.CurrentUser, ClsidClassicMenu + @"\InprocServer32"),
                ApplyClassicContextMenu),

            new UiSwitchOption(
                "show-file-extensions",
                "Расширения файлов",
                "Показывать расширения известных типов файлов. Обычно рекомендуют оставить включённым, чтобы сразу видеть настоящий тип файла.",
                [T(RegistryHive.CurrentUser, Adv, "HideFileExt", 0)],
                [T(RegistryHive.CurrentUser, Adv, "HideFileExt", 1)],
                () => ReadDword(RegistryHive.CurrentUser, Adv, "HideFileExt", 1) == 0),

            new UiSwitchOption(
                "show-hidden-files",
                "Скрытые и системные файлы",
                "Показывать скрытые файлы, папки и диски. Обычно оставляют выключенным; включайте для администрирования и диагностики.",
                [
                    T(RegistryHive.CurrentUser, Adv, "Hidden", 1),
                    T(RegistryHive.CurrentUser, Adv, "ShowSuperHidden", 1)
                ],
                [
                    T(RegistryHive.CurrentUser, Adv, "Hidden", 2),
                    T(RegistryHive.CurrentUser, Adv, "ShowSuperHidden", 0)
                ],
                () => ReadDword(RegistryHive.CurrentUser, Adv, "Hidden", 2) == 1),

            new UiSwitchOption(
                "full-path",
                "Полный путь в заголовке",
                "Адресная строка показывает полный путь вместо хлебных крошек. Обычно оставляют выключенным; включайте, если часто копируете и проверяете пути.",
                [T(RegistryHive.CurrentUser, Adv, "FullPathAddress", 1)],
                [T(RegistryHive.CurrentUser, Adv, "FullPathAddress", 0)]),

            new UiSwitchOption(
                "item-checkboxes",
                "Флажки элементов",
                "Добавляет флажки для выбора элементов в Проводнике. Обычно выключены; полезны при массовом выборе мышью или на сенсорном экране.",
                [T(RegistryHive.CurrentUser, Adv, "AutoCheckSelect", 1)],
                [T(RegistryHive.CurrentUser, Adv, "AutoCheckSelect", 0)]),

            new UiSwitchOption(
                "onedrive-nav",
                "OneDrive в навигации",
                "Значок OneDrive в дереве Проводника. Включайте при использовании OneDrive; иначе можно скрыть лишний пункт.",
                [Pin(ClsidOneDrive, 1)],
                [Pin(ClsidOneDrive, 0)],
                () => ReadDword(RegistryHive.CurrentUser, ClsidOneDrive, PinValue, 0) == 1)
        ];
    }

    private UiSwitchOption[] BuildVisualFxSwitches()
    {
        return
        [
            new UiSwitchOption(
                "animations",
                "Анимации окон и меню",
                "Сворачивание, разворачивание и анимация панели задач. Обычно оставляют включёнными; отключение имеет смысл при приоритете минимальной задержки и нагрузки на слабом ПК.",
                [
                    SZ(RegistryHive.CurrentUser, Desktop, "MinAnimate", "1"),
                    SZ(RegistryHive.CurrentUser, WindowMetrics, "MinAnimate", "1"),
                    T(RegistryHive.CurrentUser, Adv, "TaskbarAnimations", 1),
                    T(RegistryHive.CurrentUser, VisualEffects, "VisualFXSetting", 3)
                ],
                [
                    SZ(RegistryHive.CurrentUser, Desktop, "MinAnimate", "0"),
                    SZ(RegistryHive.CurrentUser, WindowMetrics, "MinAnimate", "0"),
                    T(RegistryHive.CurrentUser, Adv, "TaskbarAnimations", 0),
                    T(RegistryHive.CurrentUser, VisualEffects, "VisualFXSetting", 3)
                ],
                () => ReadString(RegistryHive.CurrentUser, Desktop, "MinAnimate", "1") == "1"),

            new UiSwitchOption(
                "transparency",
                "Прозрачность Windows",
                "Эффекты прозрачности интерфейса Windows. Обычно оставляют включёнными; отключение может уменьшить визуальные эффекты и немного снизить нагрузку на слабой графике.",
                [T(RegistryHive.CurrentUser, Personalize, "EnableTransparency", 1)],
                [T(RegistryHive.CurrentUser, Personalize, "EnableTransparency", 0)],
                () => ReadDword(RegistryHive.CurrentUser, Personalize, "EnableTransparency", 1) == 1),

            new UiSwitchOption(
                "thumbnails",
                "Эскизы вместо значков",
                "Предпросмотр изображений и видео в Проводнике. Обычно оставляют включённым; отключайте при заметной нагрузке от больших папок с медиафайлами.",
                [
                    T(RegistryHive.CurrentUser, Adv, "IconsOnly", 0),
                    T(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 1)
                ],
                [
                    T(RegistryHive.CurrentUser, Adv, "IconsOnly", 1),
                    T(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0)
                ],
                () => ReadDword(RegistryHive.CurrentUser, Adv, "IconsOnly", 0) == 0),

            new UiSwitchOption(
                "shadows",
                "Тени и полупрозрачное выделение",
                "Тени и полупрозрачное выделение элементов. Обычно оставляют включёнными; отключение подходит для упрощённого интерфейса или минимальной нагрузки на графику.",
                [
                    T(RegistryHive.CurrentUser, Adv, "ListviewShadow", 1),
                    T(RegistryHive.CurrentUser, Adv, "ListviewAlphaSelect", 1)
                ],
                [
                    T(RegistryHive.CurrentUser, Adv, "ListviewShadow", 0),
                    T(RegistryHive.CurrentUser, Adv, "ListviewAlphaSelect", 0)
                ],
                () => ReadDword(RegistryHive.CurrentUser, Adv, "ListviewShadow", 1) == 1)
        ];
    }

    public UiSwitchOption GetRecommendedSwitch()
    {
        return new UiSwitchOption(
            "recommended",
            "Раздел «Рекомендуем» в меню Пуск",
            "Показывает последние файлы и советы над закреплёнными приложениями. Обычно оставляют включённым; отключение удобно, если важнее приватность и минимальный шум в меню Пуск.",
            [],
            [
                T(RegistryHive.LocalMachine, PoliciesExplorer, "HideRecommendedSection", 1),
                T(RegistryHive.LocalMachine, PolicyManagerStart, "HideRecommendedSection", 1),
                T(RegistryHive.LocalMachine, PolicyManagerEducation, "IsEducationEnvironment", 1)
            ],
            () => ReadDword(RegistryHive.LocalMachine, PoliciesExplorer, "HideRecommendedSection", 0) != 1,
            ApplyRecommended);
    }

    // Текущая задержка меню (MenuShowDelay).
    public string GetMenuShowDelay() => ReadString(RegistryHive.CurrentUser, Desktop, "MenuShowDelay", "400");

    public Result SetMenuShowDelay(int milliseconds)
    {
        if (milliseconds is < 0 or > 1000)
        {
            return Result.Failure("Задержка меню должна быть в диапазоне 0..1000 мс.");
        }

        var result = _registry.Apply(
            [SZ(RegistryHive.CurrentUser, Desktop, "MenuShowDelay", milliseconds.ToString())],
            BackupPath("menu-delay.json"));
        return result.IsSuccess
            ? Result.Success($"Задержка меню: {milliseconds} мс.")
            : result;
    }

    public Result SetSwitch(UiSwitchOption option, bool on)
    {
        if (option.ApplyState is not null)
        {
            return option.ApplyState(on);
        }

        var tweaks = on ? option.OnTweaks : option.OffTweaks;
        if (tweaks.Count == 0)
        {
            return Result.Failure("Для «" + option.Title + "» не заданы значения реестра.");
        }

        var result = _registry.Apply(tweaks, BackupPath(option.Id + ".json"));
        if (!result.IsSuccess)
        {
            return result;
        }

        var actual = IsSwitchOn(option);
        return actual == on
            ? Result.Success(option.Title + (on ? ": включено. " : ": выключено. ") + result.Message)
            : Result.Failure($"{option.Title}: состояние не подтвердилось чтением (фактически {(actual ? "вкл" : "выкл")}).");
    }

    public bool IsSwitchOn(UiSwitchOption option) =>
        option.ReadState?.Invoke() ?? _registry.IsApplied(option.OnTweaks);

    // Классическое меню: on — ключ CLSID с пустым InprocServer32, off — ключ удаляется целиком (как в BAT).
    private Result ApplyClassicContextMenu(bool enableClassic)
    {
        try
        {
            if (enableClassic)
            {
                using var key = Registry.CurrentUser.CreateSubKey(ClsidClassicMenu + @"\InprocServer32", writable: true);
                key.SetValue(null, string.Empty, RegistryValueKind.String);
                _logger.Info("UI | classic context menu | enabled");
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClsidClassicMenu, throwOnMissingSubKey: false);
                _logger.Info("UI | classic context menu | removed");
            }
        }
        catch (Exception exception)
        {
            return Result.Failure("Контекстное меню: " + exception.Message);
        }

        return Result.Success(enableClassic
            ? "Классическое контекстное меню включено. Перезапустите проводник."
            : "Современное контекстное меню восстановлено. Перезапустите проводник.");
    }

    // «Рекомендуем»: скрытие — policy-значения с бэкапом; показ — их удаление (как в BAT).
    private Result ApplyRecommended(bool show)
    {
        if (!show)
        {
            return _registry.Apply(
            [
                T(RegistryHive.LocalMachine, PoliciesExplorer, "HideRecommendedSection", 1),
                T(RegistryHive.LocalMachine, PolicyManagerStart, "HideRecommendedSection", 1),
                T(RegistryHive.LocalMachine, PolicyManagerEducation, "IsEducationEnvironment", 1)
            ], BackupPath("recommended.json"));
        }

        try
        {
            DeleteValue(RegistryHive.LocalMachine, PoliciesExplorer, "HideRecommendedSection");
            DeleteValue(RegistryHive.LocalMachine, PolicyManagerStart, "HideRecommendedSection");
            DeleteValue(RegistryHive.LocalMachine, PolicyManagerEducation, "IsEducationEnvironment");
        }
        catch (Exception exception)
        {
            return Result.Failure("«Рекомендуем»: " + exception.Message);
        }

        return Result.Success("Раздел «Рекомендуем» показан.");
    }

    // Перезапуск проводника (taskkill /f + start explorer.exe в BAT).
    // Из повышенного процесса убиваем только проводник текущей сессии —
    // Process.GetProcessesByName без фильтра зацепил бы explorer других пользователей.
    public Result RestartExplorer()
    {
        try
        {
            var currentSession = Process.GetCurrentProcess().SessionId;
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (process.SessionId == currentSession)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Процесс мог завершиться сам.
                }

                process.Dispose();
            }

            Thread.Sleep(700);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            });

            // Верификация: проводник поднялся.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(250);
                var explorers = Process.GetProcessesByName("explorer");
                try
                {
                    if (explorers.Length > 0)
                    {
                        _logger.Info("UI | explorer restarted");
                        return Result.Success("Проводник перезапущен.");
                    }
                }
                finally
                {
                    foreach (var process in explorers)
                    {
                        process.Dispose();
                    }
                }
            }

            return Result.Failure("Проводник не перезапустился — запустите его вручную.");
        }
        catch (Exception exception)
        {
            return Result.Failure("Перезапуск проводника: " + exception.Message);
        }
    }

    // Очистка панели задач: удаление Taskband и Streams\Desktop + перезапуск (как :TaskbarClean).
    // Перед удалением — полный рекурсивный снимок ключей: операция необратима,
    // а в Taskband хранятся закреплённые значки, в Streams\Desktop — раскладка ярлыков.
    public Result ClearTaskbar()
    {
        const string taskband = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
        const string streamsDesktop = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Streams\Desktop";

        try
        {
            var backupPath = BackupPath("taskbar-cleanup.json");
            if (!File.Exists(backupPath))
            {
                var snapshot = new Dictionary<string, object>();
                SnapshotKey(Registry.CurrentUser, taskband, snapshot);
                SnapshotKey(Registry.CurrentUser, streamsDesktop, snapshot);
                Directory.CreateDirectory(_backupDirectory);
                File.WriteAllText(backupPath, System.Text.Json.JsonSerializer.Serialize(snapshot));
                _logger.Info("UI | taskbar backup | " + snapshot.Count + " значений -> " + backupPath);
            }

        Registry.CurrentUser.DeleteSubKeyTree(taskband, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(streamsDesktop, throwOnMissingSubKey: false);
        _logger.Info("UI | taskbar cleared");
        }
        catch (Exception exception)
        {
            return Result.Failure("Очистка панели задач: " + exception.Message);
        }

        return RestartExplorer();
    }

    // Восстановление панели задач из снимка taskbar-cleanup.json (пишется ClearTaskbar).
    // Ключи снимка — "путь\подключа\имя значения", каждое — { kind, data } в кодировке
    // RegistryHelper.EncodeValue; значения пишутся назад, затем проводник перезапускается.
    public Result RestoreTaskbar()
    {
        var backupPath = BackupPath("taskbar-cleanup.json");
        if (!File.Exists(backupPath))
        {
            return Result.Failure("Бэкап панели задач не найден — сначала выполните очистку из текущего профиля.");
        }

        Dictionary<string, TaskbarValueSnapshot>? snapshot;
        try
        {
            snapshot = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TaskbarValueSnapshot>>(
                File.ReadAllText(backupPath));
        }
        catch (Exception exception)
        {
            return Result.Failure("Бэкап панели задач не читается: " + exception.Message);
        }

        if (snapshot is null || snapshot.Count == 0)
        {
            return Result.Failure("Бэкап панели задач пуст.");
        }

        var restored = 0;
        var errors = new List<string>();

        // Граница записи: снимок лежит в пользовательском каталоге и мог быть
        // подменён. Восстановление вправе писать только туда, откуда ClearTaskbar
        // снимал бэкап (Taskband и Streams\Desktop, с их подключами) — иначе
        // подделанный бэкап записал бы произвольные HKCU-значения.
        var allowedPrefixes = (new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Streams\Desktop",
        }).Select(prefix => prefix.ToLowerInvariant()).ToArray();

        foreach (var (fullPath, entry) in snapshot)
        {
            // Имя значения — сегмент после последнего '\'; всё до него — путь подключа.
            var separator = fullPath.LastIndexOf('\\');
            if (separator <= 0)
            {
                continue;
            }

            var subKey = fullPath[..separator];
            var subKeyLower = subKey.ToLowerInvariant();
            if (!allowedPrefixes.Any(prefix =>
                    subKeyLower == prefix || subKeyLower.StartsWith(prefix + "\\", StringComparison.Ordinal)))
            {
                errors.Add($"{fullPath}: путь вне зоны восстановления панели задач — пропущено");
                continue;
            }

            var valueName = fullPath[(separator + 1)..];
            if (!Enum.TryParse<RegistryValueKind>(entry.Kind, out var kind) || kind == RegistryValueKind.Unknown)
            {
                errors.Add($"{fullPath}: неизвестный тип {entry.Kind}");
                continue;
            }

            try
            {
                // Декодирование внутри per-entry try: повреждённые данные бэкапа
                // (FormatException из FromBase64String) не должны ронять весь restore.
                var decoded = RegistryHelper.DecodeValue(kind, entry.Data);
                if (decoded is null)
                {
                    errors.Add($"{fullPath}: нечитаемые данные бэкапа");
                    continue;
                }

                using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
                if (key is null)
                {
                    errors.Add($"{fullPath}: не удалось создать ключ");
                    continue;
                }

                key.SetValue(valueName, decoded, kind);
                restored++;
            }
            catch (Exception exception)
            {
                errors.Add($"{fullPath}: {exception.Message}");
            }
        }

        _logger.Info($"UI | taskbar restore | restored={restored} errors={errors.Count}");
        if (restored == 0)
        {
            return Result.Failure("Не восстановлено ни одного значения: " + string.Join("; ", errors));
        }

        var restart = RestartExplorer();
        if (!restart.IsSuccess)
        {
            return restart;
        }

        var message = $"Панель задач восстановлена ({restored} значений";
        if (errors.Count > 0)
        {
            message += $", ошибок: {errors.Count}";
        }

        return Result.Success(message + ").");
    }

    private sealed class TaskbarValueSnapshot
    {
        [System.Text.Json.Serialization.JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty;
    }

    // Рекурсивный снимок ключа: "ключ\\значение" -> представление для JSON-бэкапа.
    private static void SnapshotKey(RegistryKey hive, string subKey, Dictionary<string, object> snapshot)
    {
        using var key = hive.OpenSubKey(subKey);
        if (key is null)
        {
            return;
        }

        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                continue;
            }

            var kind = key.GetValueKind(name);
            snapshot[subKey + "\\" + name] = new Dictionary<string, object>
            {
                ["kind"] = kind.ToString(),
                ["data"] = RegistryHelper.EncodeValue(value)
            };
        }

        foreach (var child in key.GetSubKeyNames())
        {
            SnapshotKey(hive, subKey + "\\" + child, snapshot);
        }
    }

    private static RegistryTweak Pin(string clsidKey, int value) =>
        T(RegistryHive.CurrentUser, clsidKey, PinValue, value);

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

    private static bool KeyExists(RegistryHive hive, string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteValue(RegistryHive hive, string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private string BackupPath(string fileName) => Path.Combine(_backupDirectory, fileName);
}
