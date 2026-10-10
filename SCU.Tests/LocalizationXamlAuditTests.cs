using System.Text.RegularExpressions;
using Xunit;

namespace SCU.Tests;

// Локализационный гейт (аудит UI): пользовательские строки обязаны жить в
// Themes/Strings.ru|en.xaml, а не в XAML-атрибутах. Инцидент: «Выбран: {0}»
// в NetworkMtuCard показывался по-русски в английском интерфейсе. Комментарии
// и файлы-словари игнорируются.
public sealed class LocalizationXamlAuditTests
{
    // Пользовательские атрибуты, где захардкоженная кириллица видна в UI.
    private static readonly Regex UserFacingAttribute = new(
        """(?:Text|Content|Title|Header|StringFormat)\s*=\s*"[^"]*[А-Яа-яЁё]""",
        RegexOptions.Compiled);

    private static string? FindAppDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "SCU.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Fact]
    public void Xaml_UserFacingAttributes_HaveNoHardcodedCyrillic()
    {
        var appDirectory = FindAppDirectory();
        Assert.False(appDirectory is null, "Каталог SCU.App не найден от тестовой сборки.");

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/Themes/Strings.ru.xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Комментарии не показываются пользователю — вырезаются до проверки.
            var text = File.ReadAllText(file);
            text = Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

            foreach (var line in text.Split('\n'))
            {
                if (UserFacingAttribute.IsMatch(line))
                {
                    violations.Add($"{Path.GetRelativePath(appDirectory, file)}: {line.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Захардкоженная кириллица в пользовательских атрибутах XAML — вынести в Strings.ru/en.xaml:\n"
            + string.Join("\n", violations));
    }
}
