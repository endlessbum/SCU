using System.Globalization;
using SCU.Infrastructure.Windows.Apps;
using SCU.ViewModels.Sections;
using Xunit;

namespace SCU.Tests;

// Размер приложений во вкладке «Приложения»: фактический — суммой файлов
// папки установки, fallback — EstimatedSize из реестра (переводится в байты
// вызывающим кодом). Формат — через L.T («244 МБ» / «244 MB»), поэтому тесты
// не привязаны к языку: проверяется число и единица из допустимых.
public class InstalledAppSizeTests
{
    private static readonly Logger Logger = Logger.CreateForCurrentRun();

    private static void AssertSizeText(string text, string number, params string[] units)
    {
        Assert.Contains(number, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(units, unit => text.Contains(unit, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(244L * 1024 * 1024, "244", "МБ", "MB")]
    [InlineData(1536L * 1024 * 1024, "1,5", "ГБ", "GB")]
    [InlineData(500L * 1024, "500", "КБ", "KB")]
    public void FormatSizeBytes_HumanReadable(long bytes, string number, string ruUnit, string enUnit)
    {
        WithRuCulture(() => AssertSizeText(InstalledAppsService.FormatSizeBytes(bytes), number, ruUnit, enUnit));
    }

    [Fact]
    public void FormatSizeBytes_Tiny_IsBelowOneKb()
    {
        var text = InstalledAppsService.FormatSizeBytes(100);

        Assert.Contains("1", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(new[] { "КБ", "KB" }, unit => text.Contains(unit, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetInstallSizeBytes_SumsAllFilesRecursively()
    {
        var root = Path.Combine(Path.GetTempPath(), "scu-app-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.bin"), new string('a', 1000));
        File.WriteAllText(Path.Combine(root, "sub", "b.bin"), new string('b', 2000));
        try
        {
            var service = new InstalledAppsService(Logger);
            var app = new InstalledApp { DisplayName = "Test", InstallLocation = root };

            var size = service.GetInstallSizeBytes(app);

            Assert.True(size >= 3000);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetInstallSizeBytes_MissingLocation_ReturnsNull()
    {
        var service = new InstalledAppsService(Logger);

        // Нет папки вообще.
        Assert.Null(service.GetInstallSizeBytes(new InstalledApp { DisplayName = "A", InstallLocation = null }));
        Assert.Null(service.GetInstallSizeBytes(new InstalledApp
        {
            DisplayName = "B",
            InstallLocation = Path.Combine(Path.GetTempPath(), "scu-no-such-dir-" + Guid.NewGuid().ToString("N")),
        }));
    }

    [Fact]
    public void GetInstallSizeBytes_SharedRootLocation_NotSummed()
    {
        // InstallLocation в реестре бывает ошибочен: если он указывает на общий
        // корень (Program Files, корень диска), суммировать его нельзя — null,
        // вызывающий код возьмёт EstimatedSize.
        var service = new InstalledAppsService(Logger);

        Assert.Null(service.GetInstallSizeBytes(new InstalledApp
        {
            DisplayName = "C",
            InstallLocation = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        }));
        Assert.Null(service.GetInstallSizeBytes(new InstalledApp
        {
            DisplayName = "D",
            InstallLocation = Path.GetPathRoot(Environment.SystemDirectory),
        }));
    }

    [Fact]
    public void Subtitle_IncludesSizePart()
    {
        var app = new InstalledApp
        {
            DisplayName = "AmneziaVPN",
            DisplayVersion = "5.0.3.0",
            SizeText = "244 МБ",
        };

        Assert.Equal("5.0.3.0 • 244 МБ", app.Subtitle);
    }

    [Fact]
    public void Subtitle_WithoutSize_Unchanged()
    {
        var app = new InstalledApp { DisplayName = "App", Publisher = "Pub", DisplayVersion = "2.0" };

        Assert.Equal("Pub • 2.0", app.Subtitle);
    }

    // ===================== Сортировка =====================

    [Theory]
    [InlineData("20240513", 2024, 5, 13)]
    [InlineData("2024-05-13", 2024, 5, 13)]
    [InlineData("13/05/2024", 2024, 5, 13)]
    [InlineData("13.05.2024", 2024, 5, 13)]
    public void ParseInstallDate_KnownFormats_Parsed(string raw, int year, int month, int day)
    {
        Assert.Equal(new DateTime(year, month, day), InstalledAppsService.ParseInstallDate(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не дата")]
    [InlineData("13/13/2024")]
    public void ParseInstallDate_Unknown_ReturnsNull(string? raw)
    {
        Assert.Null(InstalledAppsService.ParseInstallDate(raw));
    }

    private static InstalledApp App(string name, long? size = null, DateTime? date = null) =>
        new() { DisplayName = name, SizeBytes = size, InstallDate = date };

    // Форматирование размеров и сортировка по имени используют CurrentCulture —
    // это правильное поведение UI, но тесты фиксируют ru-RU: на CI локаль en-US
    // даёт «1.5» вместо «1,5» и иное упорядочение кириллицы и латиницы.
    private static void WithRuCulture(Action action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void SortApps_ByName_BothDirections()
    {
        WithRuCulture(() =>
        {
            var apps = new[] { App("бета"), App("Альфа"), App("gamma") };

            var ascending = AppsViewModel.SortApps(apps, AppSortMode.Name, ascending: true)
                .Select(app => app.DisplayName).ToArray();
            var descending = AppsViewModel.SortApps(apps, AppSortMode.Name, ascending: false)
                .Select(app => app.DisplayName).ToArray();

            Assert.Equal(new[] { "Альфа", "бета", "gamma" }, ascending);
            Assert.Equal(new[] { "gamma", "бета", "Альфа" }, descending);
        });
    }

    [Fact]
    public void SortApps_BySize_UnknownAlwaysLast()
    {
        var apps = new[] { App("big", size: 300), App("unknown"), App("small", size: 100) };

        var ascending = AppsViewModel.SortApps(apps, AppSortMode.Size, ascending: true)
            .Select(app => app.DisplayName).ToArray();
        var descending = AppsViewModel.SortApps(apps, AppSortMode.Size, ascending: false)
            .Select(app => app.DisplayName).ToArray();

        Assert.Equal(new[] { "small", "big", "unknown" }, ascending);
        Assert.Equal(new[] { "big", "small", "unknown" }, descending);
    }

    [Fact]
    public void SortApps_ByInstallDate_UnknownAlwaysLast()
    {
        var apps = new[]
        {
            App("newer", date: new DateTime(2024, 6, 1)),
            App("unknown"),
            App("older", date: new DateTime(2023, 1, 15)),
        };

        var ascending = AppsViewModel.SortApps(apps, AppSortMode.InstallDate, ascending: true)
            .Select(app => app.DisplayName).ToArray();
        var descending = AppsViewModel.SortApps(apps, AppSortMode.InstallDate, ascending: false)
            .Select(app => app.DisplayName).ToArray();

        Assert.Equal(new[] { "older", "newer", "unknown" }, ascending);
        Assert.Equal(new[] { "newer", "older", "unknown" }, descending);
    }
}
