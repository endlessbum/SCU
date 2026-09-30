using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace SCU.Common;

// Язык интерфейса. Ru — исходный язык приложения, En — полный перевод статического UI.
public enum AppLanguage
{
    Ru = 0,
    En = 1
}

// Локализация. XAML-строки лежат в Themes/Strings.ru.xaml и Strings.en.xaml
// (подмена словаря при смене языка, доступ из разметки через {DynamicResource}).
// Строки ViewModel переводятся методом T: ключом служит русская строка,
// при отсутствии перевода возвращается сама — так незаполненные места не ломают UI.
public static class L
{
    // Словари переводов теперь в LocalizationDictionaries (п. 13 аудита).
    private static readonly Dictionary<string, string> En = LocalizationDictionaries.En;
    private static readonly Dictionary<string, string> StatusEn = LocalizationDictionaries.StatusEn;
    private static readonly Dictionary<string, string> StatusTemplates = LocalizationDictionaries.StatusTemplates;
    private static readonly Dictionary<string, string> StatusFragments = LocalizationDictionaries.StatusFragments;
    private static readonly (Regex Pattern, string Template)[] StatusPatterns = BuildStatusPatterns();

    // Упорядоченный список фрагментных замен: раньше сортировка ~1000 записей
    // выполнялась на каждый вызов S() без совпадения в словарях.
    private static readonly List<KeyValuePair<string, string>> OrderedFragments = BuildOrderedFragments();

    private static List<KeyValuePair<string, string>> BuildOrderedFragments() =>
        StatusEn.Concat(StatusFragments)
            .GroupBy(x => x.Key)
            .Select(x => x.First())
            .OrderByDescending(x => x.Key.Length)
            .ToList();

    private static (Regex Pattern, string Template)[] BuildStatusPatterns()
    {
        return StatusTemplates
            .OrderByDescending(pair => pair.Key.Length)
            .Select(pair => (BuildTemplateRegex(pair.Key), pair.Value))
            .ToArray();
    }

    private static Regex BuildTemplateRegex(string template)
    {
        var builder = new System.Text.StringBuilder("^");
        var last = 0;
        foreach (Match match in Regex.Matches(template, @"\{\d+\}"))
        {
            builder.Append(Regex.Escape(template[last..match.Index]));
            builder.Append("(.*?)");
            last = match.Index + match.Length;
        }

        builder.Append(Regex.Escape(template[last..]));
        builder.Append("$");
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant);
    }

    public static string S(string text)
    {
        if (Current != AppLanguage.En || string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (En.TryGetValue(text, out var fromCatalog))
        {
            return fromCatalog;
        }

        if (StatusEn.TryGetValue(text, out var fromStatusCatalog))
        {
            return fromStatusCatalog;
        }

        foreach (var (pattern, template) in StatusPatterns)
        {
            var match = pattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var values = new object?[match.Groups.Count - 1];
            for (var i = 1; i < match.Groups.Count; i++)
            {
                values[i - 1] = S(match.Groups[i].Value);
            }

            return string.Format(template, values);
        }

        foreach (var pair in OrderedFragments)
        {
            if (text.Contains(pair.Key, StringComparison.Ordinal))
            {
                text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
            }
        }

        return text;
    }

    public static AppLanguage Current { get; private set; } = AppLanguage.Ru;

    public static event Action? LanguageChanged;

    // ===== Единый формат дат интерфейса =====
    // Все пользовательские даты — дд.мм.гггг (с временем — дд.мм.гггг чч:мм),
    // независимо от культуры потока: раньше часть мест выводила ISO (yyyy-MM-dd),
    // часть — культуру («g»), и вид даты расходился между разделами.
    // InvariantCulture: формат фиксирован, разделители не зависят от локали ОС.
    public const string DateFormat = "dd.MM.yyyy";
    public const string DateTimeFormat = "dd.MM.yyyy HH:mm";
    public const string DateTimeSecondsFormat = "dd.MM.yyyy HH:mm:ss";

    public static string Date(System.DateTime value) => value.ToString(DateFormat, System.Globalization.CultureInfo.InvariantCulture);

    // Имя метода совпадает с именем типа (как Color.Color): ссылки на тип внутри
    // класса квалифицированы через System.
    public static string DateTime(System.DateTime value) => value.ToString(DateTimeFormat, System.Globalization.CultureInfo.InvariantCulture);

    // Даты из строк внешних источников (ScannerCore пишет ISO yyyy-MM-dd).
    // Нераспознанная строка возвращается как есть — не подменяем её «01.01.0001».
    public static string Date(string? isoValue) =>
        System.DateTime.TryParseExact(
            isoValue,
            ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy.MM.dd"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed)
            ? Date(parsed)
            : isoValue ?? string.Empty;

    public static string T(string key, params object?[] args)
    {
        var text = Current == AppLanguage.En && En.TryGetValue(key, out var english) ? english : key;
        var localizedArgs = args.Length == 0
            ? args
            : args.Select(arg => arg is string value ? S(value) : arg).ToArray();
        var formatted = args.Length == 0 ? text : string.Format(text, localizedArgs);
        return Current == AppLanguage.En ? S(formatted) : formatted;
    }

    // Смена языка: подмена словаря строк XAML + уведомление подписчиков (секции перечитывают названия).
    public static void SetLanguage(AppLanguage language, bool notify = true)
    {
        if (Current == language)
        {
            return;
        }

        Current = language;
        SwapStringsDictionary();
        if (notify)
        {
            LanguageChanged?.Invoke();
        }
    }

    // Приоритет: settings.json → язык из установщика (HKCU\Software\SCU\Language) → русский.
    public static AppLanguage LoadLanguage()
    {
        try
        {
            var path = ThemeManager.GetSettingsPathPublic();
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("language", out var language))
                {
                    return Parse(language.GetString());
                }
            }

            using var key = Registry.CurrentUser.OpenSubKey(@"Software\SCU");
            if (key?.GetValue("Language") is string fromRegistry)
            {
                return Parse(fromRegistry);
            }
        }
        catch
        {
        }

        return AppLanguage.Ru;
    }

    private static AppLanguage Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "en" or "english" => AppLanguage.En,
        _ => AppLanguage.Ru
    };

    private static void SwapStringsDictionary()
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var source = new Uri(
            $"pack://application:,,,/Themes/Strings.{(Current == AppLanguage.En ? "en" : "ru")}.xaml");
        var merged = application.Resources.MergedDictionaries;

        for (var i = 0; i < merged.Count; i++)
        {
            var existing = merged[i].Source?.OriginalString?.ToLowerInvariant();
            if (existing is not null
                && (existing.EndsWith("strings.ru.xaml") || existing.EndsWith("strings.en.xaml")))
            {
                merged[i] = new ResourceDictionary { Source = source };
                return;
            }
        }

        merged.Insert(1, new ResourceDictionary { Source = source });
    }
}
