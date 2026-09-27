using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SCU.Common;

// Режим темы, выбираемый пользователем. Auto — следовать за системой.
public enum AppTheme
{
    Auto = 0,
    Light = 1,
    Dark = 2
}

// Режим прозрачности поверхностей — часть единой дизайн-системы (Liquid Glass).
// Transparent — выраженное стекло: полупрозрачные поверхности, сквозь которые
// просматривается фон. Matte — плотные матовые поверхности той же геометрии
// и той же цветовой системы.
public enum AppTransparency
{
    Transparent = 0,
    Matte = 1
}

// Акцентный цвет приложения. System — системный акцент из реестра, остальные —
// фиксированные пресеты. Пресеты заданы парой (тёмная тема, светлая тема): в тёмной
// теме текст на акценте чёрный — нужен светлый акцент, в светлой белый — тёмный.
public enum AppAccent
{
    System = 0,
    Blue = 1,
    SkyBlue = 2,
    Purple = 3,
    Green = 4,
    Orange = 5
}

// Темизация приложения: подмена словаря Colors.Dark/Colors.Light, подстановка акцента,
// применение режима прозрачности поверхностей и хранение выбора в %AppData%\SCU\settings.json
// ("theme": "auto"|"light"|"dark", "accent": ..., "transparency": "transparent"|"matte").
// Приоритет: settings.json → системная тема (AppsUseLightTheme из реестра).
public static class ThemeManager
{
    private const string SettingsDirectory = "SCU";
    private const string SettingsFileName = "settings.json";

    private const string ColorsDarkSource = "colors.dark.xaml";
    private const string ColorsLightSource = "colors.light.xaml";

    // Дефолт — синий: после установки (settings.json отсутствует) приложение
    // всегда стартует в синем акценте, а не в цвете Windows.
    public static AppAccent Accent { get; private set; } = AppAccent.Blue;

    // Отдельный цвет иконки приложения (рабочий стол, панель задач): не зависит
    // от акцентного цвета интерфейса. Дефолт — тоже синий.
    public static AppAccent IconAccent { get; internal set; } = AppAccent.Blue;

    // Поднимается при смене цвета иконки (слушает MainWindow для перерисовки значка).
    public static event Action? IconAccentChanged;

    // Режим прозрачности применяется централизованно: одни и те же ключи поверхностей
    // (glass/control/card/popup) перекрываются набором значений выбранного режима.
    public static AppTransparency Transparency { get; private set; } = AppTransparency.Transparent;

    public static event Action<bool>? ThemeApplied;

    // Поднимается при каждом применении акцента (SetAccent и ApplyTheme) — на него
    // подписываются зависящие от акцентного цвета элементы (иконка приложения).
    public static event Action? AccentChanged;

    // Тот же цвет, что заливается в AccentFillBrush: тёмная/светлая тема даёт
    // разные варианты пресетов, системный акцент читается из реестра.
    // Кэшируется: раньше каждый запрос читал settings.json и реестр (диск на UI-потоке).
    public static Color CurrentAccentColor
    {
        get
        {
            var dark = IsDarkTheme(GetCachedThemeMode());
            if (_cachedAccentColor is { } color && _cachedAccentDark == dark)
            {
                return color;
            }

            color = GetAccentColor(dark);
            _cachedAccentColor = color;
            _cachedAccentDark = dark;
            return color;
        }
    }

    // Цвет иконки приложения: тот же расчёт пресетов, но по IconAccent, а не Accent.
    public static Color CurrentIconAccentColor
    {
        get
        {
            var dark = IsDarkTheme(GetCachedThemeMode());
            return GetPresetColor(IconAccent, dark);
        }
    }

    private static AppTheme? _cachedThemeMode;
    private static Color? _cachedAccentColor;
    private static bool _cachedAccentDark;

    private static void InvalidateCaches()
    {
        _cachedThemeMode = null;
        _cachedAccentColor = null;
    }

    private static AppTheme GetCachedThemeMode()
    {
        _cachedThemeMode ??= LoadThemeMode();
        return _cachedThemeMode.Value;
    }

    // Приёмник предупреждений (настраивается на Logger при старте приложения):
    // раньше ошибки записи настроек глотались полностью немо.
    internal static Action<string>? LogWarningSink { get; set; }

    // Следит за сменой темы/акцента Windows в рантайме: Auto-тема и системный
    // акцент применяются сразу, а не при следующем действии пользователя.
    public static void StartSystemThemeWatcher()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Смена AppsUseLightTheme/ColorizationColor приходит как General.
        if (e.Category is not UserPreferenceCategory.General)
        {
            return;
        }

        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        // Событие может прийти с фонового потока SystemEvents — только в UI-поток.
        application.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                InvalidateCaches();
                if (GetCachedThemeMode() == AppTheme.Auto)
                {
                    ApplyTheme(AppTheme.Auto);
                }
                else if (Accent == AppAccent.System)
                {
                    ApplyAccentBrushes(IsDarkTheme(GetCachedThemeMode()));
                }
            }
            catch (Exception exception)
            {
                LogWarningSink?.Invoke("THEME | system preference change failed | " + exception.Message);
            }
        });
    }

    // Пресеты акцента — системная палитра iOS: (тёмная тема, светлая тема).
    private static readonly Dictionary<AppAccent, (Color Dark, Color Light)> AccentPresets = new()
    {
        [AppAccent.Blue] = (Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x00, 0x7A, 0xFF)),
        [AppAccent.SkyBlue] = (Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x32, 0xAD, 0xE6)),
        [AppAccent.Purple] = (Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0xAF, 0x52, 0xDE)),
        [AppAccent.Green] = (Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0x34, 0xC7, 0x59)),
        [AppAccent.Orange] = (Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x95, 0x00))
    };

    public static bool IsDarkTheme(AppTheme mode) => mode switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => IsSystemDarkTheme()
    };

    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            // Единый fallback с отсутствующим ключом: светлая тема (значение
            // Windows по умолчанию). Раньше исключение давало тёмную, отсутствие
            // ключа — светлую: поведение расходилось.
            return false;
        }
    }

    public static AppTheme LoadThemeMode()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return AppTheme.Auto;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var raw = document.RootElement.TryGetProperty("theme", out var theme)
                ? theme.GetString()
                : null;
            return raw?.ToLowerInvariant() switch
            {
                "light" => AppTheme.Light,
                "dark" => AppTheme.Dark,
                _ => AppTheme.Auto
            };
        }
        catch
        {
            return AppTheme.Auto;
        }
    }

    public static AppAccent LoadAccentMode()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return AppAccent.Blue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var raw = document.RootElement.TryGetProperty("accent", out var accent)
                ? accent.GetString()
                : null;
            return raw?.ToLowerInvariant() switch
            {
                "blue" => AppAccent.Blue,
                "skyblue" => AppAccent.SkyBlue,
                "purple" => AppAccent.Purple,
                "green" => AppAccent.Green,
                "orange" => AppAccent.Orange,
                // Явное legacy-значение: раньше был пункт «Как в системе», мигрируем на синий.
                "system" => AppAccent.Blue,
                // Нераспознанное значение — тот же fallback, что у отсутствующего
                // файла и сбоя чтения: дефолтный синий.
                _ => AppAccent.Blue
            };
        }
        catch
        {
            return AppAccent.Blue;
        }
    }

    // Цвет иконки приложения: значение из настроек; параметр отсутствует — синий.
    public static AppAccent LoadIconAccent()
    {
        try
        {
            var path = GetSettingsPath();
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var raw = document.RootElement.TryGetProperty("iconAccent", out var iconAccent)
                    ? iconAccent.GetString()
                    : null;
                return raw?.ToLowerInvariant() switch
                {
                    "blue" => AppAccent.Blue,
                    "skyblue" => AppAccent.SkyBlue,
                    "purple" => AppAccent.Purple,
                    "green" => AppAccent.Green,
                    "orange" => AppAccent.Orange,
                    _ => AppAccent.Blue
                };
            }
        }
        catch
        {
        }

        return AppAccent.Blue;
    }

    // Смена цвета иконки: применяется сразу и сохраняется в settings.json.
    public static void SetIconAccent(AppAccent accent)
    {
        if (IconAccent == accent)
        {
            return;
        }

        IconAccent = accent;
        IconAccentChanged?.Invoke();
    }

    // Режим прозрачности: значение из настроек; параметр отсутствует — «Тусклый»
    // нераспознанное значение — тот же fallback.
    public static AppTransparency LoadTransparency()
    {
        try
        {
            var path = GetSettingsPath();
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("transparency", out var transparency))
                {
                    return transparency.GetString()?.ToLowerInvariant() switch
                    {
                        "matte" => AppTransparency.Matte,
                        _ => AppTransparency.Transparent
                    };
                }
            }
        }
        catch
        {
        }

        return AppTransparency.Transparent;
    }

    // Переключение режима прозрачности в рантайме: поверхности перекрашиваются,
    // выбор сохраняется в settings.json (вместе с остальными настройками).
    public static void SetTransparency(AppTransparency transparency)
    {
        Transparency = transparency;
        ApplySurfaceBrushes(IsDarkTheme(GetCachedThemeMode()), transparency);
        SaveSettings(GetCachedThemeMode(), Accent, L.Current, LoadShowLog(), LoadUacConfirmations());
    }

    // Глобальные горячие клавиши (Ctrl+Alt+S — показать/скрыть SCU): переключаются
    // в «Настройки → Горячие клавиши», хранятся в общем settings.json.
    public static bool GlobalHotkeysEnabled { get; internal set; } = true;

    public static void SaveSettings(AppTheme theme, AppAccent accent, AppLanguage language, bool showLog, bool uacConfirmations = true)
    {
        InvalidateCaches();
        try
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(
                new
                {
                    theme = theme.ToString().ToLowerInvariant(),
                    accent = accent.ToString().ToLowerInvariant(),
                    transparency = Transparency.ToString().ToLowerInvariant(),
                    language = language == AppLanguage.En ? "en" : "ru",
                    showLog = showLog,
                    uacConfirmations = uacConfirmations,
                    globalHotkeys = GlobalHotkeysEnabled,
                    iconAccent = IconAccent.ToString().ToLowerInvariant()
                },
                new JsonSerializerOptions { WriteIndented = true });

            // Атомарная запись (temp + move): сбой посреди прямой записи оставил бы
            // повреждённый settings.json, и все настройки откатились бы на дефолты.
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception)
        {
            // Настройки — вспомогательная функциональность: не роняем приложение,
            // но и не молчим — причина уходит в журнал.
            LogWarningSink?.Invoke("SETTINGS | save failed | " + exception.Message);
        }
    }

    public static string GetSettingsPathPublic() => GetSettingsPath();

    // Подтверждения рисковых действий (тумблер «UAC»): значение из настроек;
    // параметр отсутствует — подтверждения включены.
    public static bool LoadUacConfirmations()
    {
        try
        {
            var path = GetSettingsPath();
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("uacConfirmations", out var uac)
                    && (uac.ValueKind == JsonValueKind.True || uac.ValueKind == JsonValueKind.False))
                {
                    return uac.GetBoolean();
                }
            }
        }
        catch
        {
        }

        return true;
    }

    // Глобальные горячие клавиши: значение из настроек; параметр отсутствует — включены.
    public static bool LoadGlobalHotkeys()
    {
        try
        {
            var path = GetSettingsPath();
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("globalHotkeys", out var hotkeys)
                    && (hotkeys.ValueKind == JsonValueKind.True || hotkeys.ValueKind == JsonValueKind.False))
                {
                    return hotkeys.GetBoolean();
                }
            }
        }
        catch
        {
        }

        return true;
    }

    // Видимость журнала: значение из настроек; параметр отсутствует — журнал видим.
    public static bool LoadShowLog()
    {
        try
        {
            var path = GetSettingsPath();
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("showLog", out var log)
                    && (log.ValueKind == JsonValueKind.True || log.ValueKind == JsonValueKind.False))
                {
                    return log.GetBoolean();
                }
            }
        }
        catch
        {
        }

        return true;
    }

    // Выбор акцента без смены темы: пресеты применяются немедленно.
    public static void SetAccent(AppAccent accent)
    {
        Accent = accent;
        InvalidateCaches();
        ApplyAccentBrushes(IsDarkTheme(GetCachedThemeMode()));
    }

    // Подменяет словарь цветов в MergedDictionaries, подставляет акцент
    // и уведомляет окна (DWM dark mode должен переключиться синхронно с темой).
    public static void ApplyTheme(AppTheme mode)
    {
        var dark = IsDarkTheme(mode);
        InvalidateCaches();
        SwapColorDictionary(dark);
        ApplySurfaceBrushes(dark, Transparency);
        ApplyAccentBrushes(dark);
        ThemeApplied?.Invoke(dark);
    }

    // Поверхностные кисти двух режимов прозрачности — две версии одной дизайн-системы:
    // та же геометрия и та же палитра, отличается только плотность/прозрачность поверхности.
    // Transparent — выраженное стекло (низкая альфа), Matte — плотные матовые поверхности.
    private static readonly Dictionary<string, (Color Transparent, Color Matte)> SurfaceLight = new()
    {
        ["PanelBackgroundBrush"] = (Color.FromArgb(0x59, 0xF9, 0xF9, 0xFB), Color.FromArgb(0xF2, 0xF7, 0xF7, 0xFA)),
        ["GlassFillBrush"] = (Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF)),
        ["GlassFillStrongBrush"] = (Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        ["SectionCardPrimaryFillBrush"] = (Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        ["SectionCardSecondaryFillBrush"] = (Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xF2, 0xFB, 0xFB, 0xFD)),
        ["ControlFillBrush"] = (Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        ["ControlFillSecondaryBrush"] = (Color.FromArgb(0xA6, 0xF2, 0xF2, 0xF7), Color.FromArgb(0xFF, 0xF6, 0xF6, 0xF8)),
        ["ControlFillTertiaryBrush"] = (Color.FromArgb(0x8C, 0xE4, 0xE4, 0xEA), Color.FromArgb(0xFF, 0xED, 0xED, 0xF0)),
        ["PopupBackgroundBrush"] = (Color.FromArgb(0xF2, 0xFC, 0xFC, 0xFE), Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
    };

    // Тёмная палитра: #202020 — фон, #2B2B2B — плашки/панели и окна функций,
    // #363636 — плашка в плашке (вложенные поверхности, popups) и кнопки;
    // вкладка меню — #383838.
    private static readonly Dictionary<string, (Color Transparent, Color Matte)> SurfaceDark = new()
    {
        ["PanelBackgroundBrush"] = (Color.FromArgb(0x99, 0x2B, 0x2B, 0x2B), Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B)),
        // Окна функций — те же значения, что у панели меню: карточки разделов
        // и строки-карточки с тумблерами визуально совпадают с сайдбаром.
        ["GlassFillBrush"] = (Color.FromArgb(0x99, 0x2B, 0x2B, 0x2B), Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B)),
        ["GlassFillStrongBrush"] = (Color.FromArgb(0x99, 0x2B, 0x2B, 0x2B), Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B)),
        ["SectionCardPrimaryFillBrush"] = (Color.FromArgb(0xB3, 0x2B, 0x2B, 0x2B), Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B)),
        ["SectionCardSecondaryFillBrush"] = (Color.FromArgb(0x66, 0x36, 0x36, 0x36), Color.FromArgb(0xF2, 0x36, 0x36, 0x36)),
        ["ControlFillBrush"] = (Color.FromArgb(0x80, 0x36, 0x36, 0x36), Color.FromArgb(0xFF, 0x36, 0x36, 0x36)),
        ["ControlFillSecondaryBrush"] = (Color.FromArgb(0x99, 0x36, 0x36, 0x36), Color.FromArgb(0xFF, 0x36, 0x36, 0x36)),
        // Hover/pressed: заметно ярче заливки, иначе наведение не читается.
        ["ControlFillTertiaryBrush"] = (Color.FromArgb(0xFF, 0x41, 0x41, 0x41), Color.FromArgb(0xFF, 0x41, 0x41, 0x41)),
        ["PopupBackgroundBrush"] = (Color.FromArgb(0xF2, 0x36, 0x36, 0x36), Color.FromArgb(0xFF, 0x36, 0x36, 0x36))
    };

    // Централизованное применение режима прозрачности: перекрывает поверхностные кисти
    // словаря темы набором значений выбранного режима. Один переключатель управляет
    // всеми основными surfaces сразу — индивидуальных настроек прозрачности нет.
    private static void ApplySurfaceBrushes(bool dark, AppTransparency transparency)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var surfaces = dark ? SurfaceDark : SurfaceLight;
        foreach (var (key, modes) in surfaces)
        {
            SetBrush(application, key, transparency == AppTransparency.Matte ? modes.Matte : modes.Transparent);
        }
    }

    private static void SwapColorDictionary(bool dark)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var source = new Uri(
            $"pack://application:,,,/Themes/Colors.{(dark ? "Dark" : "Light")}.xaml");
        var merged = application.Resources.MergedDictionaries;

        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source?.OriginalString?.ToLowerInvariant() is { } existing
                && (existing.EndsWith(ColorsDarkSource) || existing.EndsWith(ColorsLightSource)))
            {
                merged[i] = new ResourceDictionary { Source = source };
                return;
            }
        }

        merged.Insert(0, new ResourceDictionary { Source = source });
    }

    // Акцент подставляется поверх fallback-значений словаря: заливка, hover/pressed
    // (светлее/темнее), полупрозрачные тонировка и обводка.
    private static void ApplyAccentBrushes(bool dark)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var accent = GetAccentColor(dark);
        var hover = dark ? Shift(accent, 1.12f) : Shift(accent, 0.88f);
        var pressed = dark ? Shift(accent, 0.9f) : Shift(accent, 0.78f);

        SetBrush(application, "AccentFillBrush", accent);
        SetBrush(application, "AccentHoverBrush", hover);
        SetBrush(application, "AccentPressedBrush", pressed);
        SetBrush(application, "AccentTintBrush", Color.FromArgb((byte)(dark ? 0x2E : 0x1A), accent.R, accent.G, accent.B));
        SetBrush(application, "AccentStrokeBrush", Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        // Текст на акцентных кнопках: светлый акцент — чёрный текст, тёмный — белый.
        // Порог 140 сохраняет дефолты обеих тем (#005FB8 -> белый, #4CC2FF -> чёрный)
        // и пересчитывается при системном акценте пользователя.
        SetBrush(application, "AccentTextBrush", PerceivedLuminance(accent) > 140 ? Colors.Black : Colors.White);
        // Выделенная вкладка меню: в светлой теме — акцентная тонировка, как раньше;
        // в тёмной — нейтральная плашка #2B2B2B (фикс, от акцента не зависит).
        SetBrush(
            application,
            "NavSelectedFillBrush",
            dark
                ? Color.FromArgb(0xFF, 0x38, 0x38, 0x38)
                : Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
        FreezeThemeBrushes(application);
        AccentChanged?.Invoke();
    }

    // Относительная яркость по Rec.709 (0..255), без гамма-коррекции —
    // для выбора чёрного/белого текста точности достаточно.
    internal static double PerceivedLuminance(Color color) =>
        0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;

    private static void FreezeThemeBrushes(Application application)
    {
        FreezeDictionaryBrushes(application.Resources);
        foreach (var dictionary in application.Resources.MergedDictionaries)
        {
            FreezeDictionaryBrushes(dictionary);
        }
    }

    private static void FreezeDictionaryBrushes(ResourceDictionary dictionary)
    {
        foreach (var value in dictionary.Values)
        {
            if (value is SolidColorBrush brush && brush.CanFreeze)
            {
                brush.Freeze();
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            FreezeDictionaryBrushes(merged);
        }
    }

    // Цвет пресета для произвольного режима акцента (выбор в «Настройках»).
    public static Color GetPresetColor(AppAccent accent, bool dark) => accent switch
    {
        AppAccent.System => GetSystemAccent(dark),
        _ when AccentPresets.TryGetValue(accent, out var preset) => dark ? preset.Dark : preset.Light,
        _ => GetSystemAccent(dark)
    };

    // Системный акцент Windows (fallback для AppAccent.System).
    private static Color GetSystemAccent(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("ColorizationColor") is int raw)
            {
                // Реестр отдаёт цвет в формате AABBGGRR, но системная альфа может быть меньше 0xFF.
                // Для акцентной кисти приложения цвет должен быть полностью непрозрачным.
                return Color.FromArgb(
                    0xFF,
                    (byte)(raw & 0xFF),
                    (byte)((raw >> 8) & 0xFF),
                    (byte)((raw >> 16) & 0xFF));
            }
        }
        catch
        {
        }

        return dark ? AccentPresets[AppAccent.Blue].Dark : AccentPresets[AppAccent.Blue].Light;
    }

    private static Color GetAccentColor(bool dark) => GetPresetColor(Accent, dark);

    private static void SetBrush(Application application, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        application.Resources[key] = brush;
    }

    internal static Color Shift(Color color, float factor)
    {
        return Color.FromRgb(
            Clamp(color.R * factor),
            Clamp(color.G * factor),
            Clamp(color.B * factor));
    }

    internal static byte Clamp(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static string GetSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        SettingsDirectory,
        SettingsFileName);
}
