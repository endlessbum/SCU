using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SCU.Common;

namespace SCU.Infrastructure.Networking;

// HTTPS-загрузчик подписанных пакетов базы (документ п. 30):
// скачать во временный файл → установка через ScannerCore update
// (проверка подписи/схемы и atomic replace — в C++-стороне, п. 33).
// Разрешены только https-адреса; http — только для loopback (тесты/локальная
// раздача), сетевые http-загрузки запрещены принципиально.

public sealed class ScannerUpdateService
{
    // Адрес по умолчанию — ассет database-latest.zip на фиксированном теге
    // scanner-db (rolling pre-release, публикуется database.yml ежедневно).
    // П. releases-аудита: прежде URL был releases/latest/download/... — он
    // завязывал «Latest» на релиз базы, и ежедневный db-* релиз перетягивал
    // плашку Latest у релизов приложения. Фиксированный тег от Latest не
    // зависит; прямой /releases/download/... отдаёт ассеты и pre-release.
    // Переопределяется файлом %APPDATA%\SCU\scanner-update.json ({"url": "..."}).
    public const string DefaultUrl =
        "https://github.com/endlessbum/SCU/releases/download/scanner-db/database-latest.zip";

    private const int MaxRedirects = 3;

    // Верхняя граница размера пакета базы: защищает диск от гигантского/обрывочного ответа.
    private const long MaxPackageBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly Logger? _logger;

    public ScannerUpdateService(Logger? logger = null)
    {
        _logger = logger;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            // Redirect'ы НЕ следуются автоматически (п. 8 аудита): каждый
            // переход проверяется вручную — только HTTPS (loopback-исключение
            // для тестов), без понижения https→http, без смены хоста.
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    // Адрес db-version.json рядом с пакетом: из URL пакета заменой имени файла.
    // null — адрес не разобрать (вызывающий код скачивает пакет без сверки).
    internal static string? DeriveVersionUrl(string packageUrl)
    {
        if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.LocalPath;
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        return new Uri(uri, path[..^fileName.Length] + "db-version.json").ToString();
    }

    // Версия базы на релизе — маленький ассет db-version.json рядом с пакетом.
    // Сверка до скачивания избавляет от перекачивания пакета целиком при каждом
    // старте. Сбой (нет ассета, сети) — обычный Failure: вызывающий код ведёт
    // себя как раньше, то есть скачивает пакет.
    public async Task<Result<string>> GetLatestDbVersionAsync(CancellationToken ct = default)
    {
        var versionUrl = DeriveVersionUrl(ResolveUrl());
        if (versionUrl is null)
        {
            return Result<string>.Failure("Не удалось определить адрес версии базы.", 2);
        }

        try
        {
            // Ручные redirect — те же ограничения, что у пакета (п. 8 аудита).
            var current = new Uri(versionUrl);
            HttpResponseMessage? response = null;
            for (var redirect = 0; ; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode))
                {
                    break;
                }

                using (response)
                {
                    if (redirect >= MaxRedirects)
                    {
                        return Result<string>.Failure("Превышен лимит перенаправлений.", 2);
                    }

                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        return Result<string>.Failure("Redirect без адреса Location.", 2);
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!IsAllowedRedirect(current, next))
                    {
                        return Result<string>.Failure("Недопустимый redirect (только HTTPS, без смены хоста и понижения схемы).", 2);
                    }

                    current = next;
                }
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var version = json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("version", out var value)
                    && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;
                return string.IsNullOrWhiteSpace(version)
                    ? Result<string>.Failure("db-version.json без версии.", 2)
                    : Result<string>.Success(version);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<string>.Failure("Отменено", -1);
        }
        catch (Exception exception)
        {
            // Сетевые и JSON-ошибки: версию узнать не удалось — это не сбой
            // обновления, пакет качается без сверки.
            _logger?.Warn("SCANNER UPDATE | version probe failed | " + exception.Message);
            return Result<string>.Failure(exception.Message);
        }
    }

    public string ResolveUrl()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCU", "scanner-update.json");
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<UpdateConfig>(File.ReadAllText(path), JsonOptions);
                if (!string.IsNullOrWhiteSpace(config?.Url))
                {
                    // Файл в %AppData% может изменить любой процесс того же
                    // пользователя: без проверки хоста переопределение уводило бы
                    // автообновление базы (стартует со сканером) на чужой сервер.
                    if (IsAllowedOverride(config.Url))
                    {
                        return config.Url;
                    }

                    _logger?.Warn("SCANNER UPDATE | override refused (untrusted host) | " + config.Url);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }

        return DefaultUrl;
    }

    // Переопределение разрешено только доверенным хостам: github.com и его
    // release-CDN (те же, что для redirect'ов), loopback — для тестов.
    internal static bool IsAllowedOverride(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return IsLoopback(uri);
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || IsGitHubCdnHost(uri.Host)
            || IsLoopback(uri);
    }

    public async Task<Result<string>> DownloadPackageAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !IsLoopback(uri)))
        {
            return Result<string>.Failure(
                "Загрузка разрешена только по HTTPS (или с localhost для теста).", 2);
        }

        string tempPath;
        try
        {
            tempPath = Path.Combine(Path.GetTempPath(), "scu-db-" + Guid.NewGuid().ToString("N") + ".zip");
        }
        catch (IOException exception)
        {
            return Result<string>.Failure("Не удалось создать временный файл: " + exception.Message);
        }

        try
        {
            // Ручная обработка redirect (п. 8 аудита): максимум MaxRedirects,
            // только HTTPS (loopback http — для локальной раздачи), схема не
            // понижается, хост не меняется. Нарушение — отказ, а не переход.
            var current = uri;
            HttpResponseMessage? response = null;
            for (var redirect = 0; ; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode))
                {
                    break;
                }

                using (response)
                {
                    if (redirect >= MaxRedirects)
                    {
                        return Fail(tempPath, "Превышен лимит перенаправлений.");
                    }

                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        return Fail(tempPath, "Redirect без адреса Location.");
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!IsAllowedRedirect(current, next))
                    {
                        return Fail(tempPath, "Недопустимый redirect (только HTTPS, без смены хоста и понижения схемы).");
                    }

                    current = next;
                }
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();

                // Лимит размера: обрывочный/гигантский ответ не должен заполнять диск.
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength is > MaxPackageBytes)
                {
                    return Fail(tempPath, $"Пакет базы слишком велик ({contentLength.Value / (1024 * 1024)} МБ, лимит {MaxPackageBytes / (1024 * 1024)} МБ).");
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = File.Create(tempPath);
                // CopyToAsync без ограничения писало бы в temp сколько угодно: копируем
                // с подсчётом и обрываем при превышении лимита (Content-Length может
                // отсутствовать или врать).
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await responseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes)
                    {
                        return Fail(tempPath, $"Пакет базы превышает лимит {MaxPackageBytes / (1024 * 1024)} МБ — загрузка прервана.");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            return Result<string>.Success(tempPath, tempPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(tempPath);
            return Result<string>.Failure("Отменено", -1);
        }
        catch (OperationCanceledException exception) when (exception.InnerException is TimeoutException)
        {
            // HttpClient.Timeout приходит как TaskCanceledException(TimeoutException):
            // иначе пользователь ручного обновления видит «Отменено» вместо таймаута.
            TryDelete(tempPath);
            return Result<string>.Failure(
                $"Превышено время ожидания сервера ({(int)_httpClient.Timeout.TotalMinutes} мин).", -2);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return Result<string>.Failure("Отменено", -1);
        }
        catch (HttpRequestException exception)
        {
            TryDelete(tempPath);
            return Result<string>.Failure("Ошибка загрузки пакета: " + exception.Message);
        }
        catch (IOException exception)
        {
            TryDelete(tempPath);
            return Result<string>.Failure("Ошибка записи пакета: " + exception.Message);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    // Разрешён переход на тот же хост либо на CDN GitHub (github.com отдаёт
    // ассеты через objects.githubusercontent.com), схема не понижается
    // (https→http запрещён всегда). Исключение loopback — для тестов.
    internal static bool IsAllowedRedirect(Uri from, Uri to)
    {
        if (to.Scheme == Uri.UriSchemeHttps)
        {
            return string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)
                || IsGitHubCdnHost(to.Host);
        }

        return to.Scheme == Uri.UriSchemeHttp
            && !from.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && IsLoopback(to)
            && string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static Result<string> Fail(string tempPath, string message)
    {
        TryDelete(tempPath);
        return Result<string>.Failure(message, 2);
    }

    private static bool IsLoopback(Uri uri)
    {
        // Uri.Host для "http://[::1]/" возвращает "[::1]" со скобками — снимаем их,
        // иначе IPv6-loopback не распознавался и локальная раздача запрещалась.
        var host = uri.Host;
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        return host is "localhost" or "127.0.0.1" or "::1";
    }

    private static bool IsGitHubCdnHost(string host) =>
        host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed class UpdateConfig
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
