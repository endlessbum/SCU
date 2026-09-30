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
// Последний релиз может быть датным релизом базы (db-ГГГГ.ММ.ДД, публикуется
// database.yml с флагом --latest, чтобы автообновление базы попадало на
// releases/latest/download/database-latest.zip). Поэтому читается список релизов,
// и версией приложения считается первый тег, разбираемый как версия: db-* пропускаются.
// Сравнение — по трём частям версии сборки (2.2.0), тег допускает префикс «v».
public sealed class UpdateCheckService
{
    public const string ReleasesPageUrl = "https://github.com/endlessbum/SCU/releases";
    private const string ReleasesListUrl =
        "https://api.github.com/repos/endlessbum/SCU/releases?per_page=10";

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
            var tags = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray()
                    .Select(release => release.TryGetProperty("tag_name", out var tagName)
                        ? tagName.GetString()
                        : null)
                : Enumerable.Empty<string?>();

            var chosenTag = SelectVersionTag(tags);
            if (chosenTag is null)
            {
                return new UpdateCheckResult(false, false, null, CurrentVersion, ReleasesPageUrl,
                    "в релизах не найдена версия приложения");
            }

            // Пререлизы (тег с суффиксом «-…») обновлением не предлагаются.
            var isPrerelease = chosenTag.Contains('-', StringComparison.Ordinal);

            var latest = NormalizeVersion(chosenTag)!;

            var current = Version.TryParse(CurrentVersion, out var parsed) ? parsed : new Version(0, 0, 0);
            return new UpdateCheckResult(
                true,
                !isPrerelease && latest > current,
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

    // Первый тег, разбираемый как версия приложения, в порядке свежести релизов.
    // Датные релизы базы (db-…) пропускаются: это не версии SCU.
    internal static string? SelectVersionTag(IEnumerable<string?> tags)
    {
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            if (tag.StartsWith("db-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (NormalizeVersion(tag) is null)
            {
                continue;
            }

            return tag;
        }

        return null;
    }

    // «v2.3.1» / «2.3.1» / «2.3» → Version; null, если разобрать не удалось.
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

        return Version.TryParse(trimmed, out var version) ? version : null;
    }
}
