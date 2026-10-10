using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using SCU.Common;

namespace SCU.Infrastructure.Networking;

// Результат проверки обновлений.
public sealed record UpdateCheckResult(
    bool Success,
    bool HasUpdate,
    string? LatestVersion,
    string CurrentVersion,
    string ReleaseUrl,
    string Error);

// Проверка обновлений SCU по GitHub Releases.
// П. releases-аудита: раньше база сканера публиковалась как обычный релиз
// db-ГГГГ.М.ДД с флагом --latest и перетягивала плашку «Latest» у приложения.
// Теперь база — rolling pre-release с фиксированным тегом scanner-db
// (database.yml), и «Latest» всегда принадлежит релизу SCU.
// Выбор версии (аудит обновлений): из всех релизов выбирается МАКСИМАЛЬНАЯ
// стабильная версия по объектам релизов, а не первый подходящий тег списка —
// иначе prerelease, опубликованный позже stable, загораживал её и обновление
// не предлагалось. Статус prerelease/draft берётся из метаданных GitHub, а не
// из наличия «-» в теге. Сравнение — по трём частям версии сборки (2.2.0),
// тег допускает префикс «v».
public sealed class UpdateCheckService
{
    public const string ReleasesPageUrl = "https://github.com/endlessbum/SCU/releases";
    // per_page=100: узкая страница пропускала свежий стабильный релиз, если
    // после него вышло больше пререлизов/служебных тегов, чем влезло в выборку.
    private const string ReleasesListUrl =
        "https://api.github.com/repos/endlessbum/SCU/releases?per_page=100";

    // Минимум полей релиза GitHub, нужных для выбора версии приложения.
    internal sealed record GitHubReleaseInfo(string? TagName, bool Draft, bool Prerelease);

    public string CurrentVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0");

    // Общий клиент: без сокет-истощения от новых HttpClient на каждый вызов.
    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API требует User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SCU-App/update-check");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await SharedClient.GetAsync(ReleasesListUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, false, null, CurrentVersion, ReleasesPageUrl,
                    $"GitHub API: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var releases = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray()
                    .Select(release => new GitHubReleaseInfo(
                        release.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null,
                        release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                        release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True))
                : Enumerable.Empty<GitHubReleaseInfo>();

            var chosen = SelectLatestStableRelease(releases);
            if (chosen is null)
            {
                return new UpdateCheckResult(false, false, null, CurrentVersion, ReleasesPageUrl,
                    "в релизах не найдена версия приложения");
            }

            var latest = NormalizeVersion(chosen.TagName)!;

            var current = Version.TryParse(CurrentVersion, out var parsed) ? parsed : new Version(0, 0, 0);
            return new UpdateCheckResult(
                true,
                latest > current,
                latest.ToString(),
                CurrentVersion,
                ReleasesPageUrl,
                string.Empty);
        }
        catch (Exception exception)
        {
            // Сетевые ошибки — понятным пользователю текстом, не стеком.
            var message = exception is TaskCanceledException or HttpRequestException
                ? L.T("нет соединения с GitHub")
                : exception.Message;
            return new UpdateCheckResult(false, false, null, CurrentVersion, ReleasesPageUrl, message);
        }
    }

    // Максимальная стабильная версия среди релизов: draft/prerelease (по
    // метаданным GitHub) и релизы базы (db-*, scanner-db) пропускаются,
    // остальные сравниваются по нормализованной версии. Порядок в списке
    // GitHub — по дате публикации, поэтому «первый подходящий» мог выбрать
    // prerelease и скрыть более новую stable.
    internal static GitHubReleaseInfo? SelectLatestStableRelease(IEnumerable<GitHubReleaseInfo> releases)
    {
        GitHubReleaseInfo? best = null;
        Version? bestVersion = null;
        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease)
            {
                continue;
            }

            var tag = release.TagName;
            if (string.IsNullOrWhiteSpace(tag)
                || tag.StartsWith("db-", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith("scanner-db", StringComparison.OrdinalIgnoreCase)
                || NormalizeVersion(tag) is not { } version)
            {
                continue;
            }

            if (bestVersion is null || version > bestVersion)
            {
                best = release;
                bestVersion = version;
            }
        }

        return best;
    }

    // «v2.3.1» / «2.3.1» / «2.3» → Version; null, если разобрать не удалось.
    // Двухкомпонентный тег нормализуется до трёх частей: System.Version
    // сравнивает покомпонентно, и без этого «2.3» оказалась бы меньше «2.3.0».
    internal static Version? NormalizeVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var trimmed = tag.Trim().TrimStart('v', 'V');
        // Отрезаем возможные суффиксы сборки: «2.3.1-beta1+build» → «2.3.1-beta1».
        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        var dash = trimmed.IndexOf('-');
        if (dash >= 0)
        {
            trimmed = trimmed[..dash];
        }

        if (!Version.TryParse(trimmed, out var version))
        {
            return null;
        }

        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
