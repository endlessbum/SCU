using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SCU.Common;

namespace SCU.Services;

// HTTPS-загрузчик подписанных пакетов базы (документ п. 30):
// скачать во временный файл → установка через ScannerCore update
// (проверка подписи/схемы и atomic replace — в C++-стороне, п. 33).
// Разрешены только https-адреса; http — только для loopback (тесты/локальная
// раздача), сетевые http-загрузки запрещены принципиально.

public sealed class ScannerUpdateService
{
    // Адрес по умолчанию задаёт издатель при развёртывании; переопределяется
    // файлом %APPDATA%\SCU\scanner-update.json ({"url": "..."}).
    // ВНИМАНИЕ: .local — placeholder. Production-сборка обязана задавать
    // боевой endpoint этим конфигом (п. 9 аудита); выпускать прод с .local
    // запрещено.
    public const string DefaultUrl = "https://updates.scu.local/database-latest.zip";

    private const int MaxRedirects = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public ScannerUpdateService()
    {
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
                    return config.Url;
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
                await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = File.Create(tempPath);
                await responseStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            return Result<string>.Success(tempPath, tempPath);
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

    // Разрешён переход только на тот же хост, схема не понижается (https→http
    // запрещён всегда). Исключение loopback — для тестов/локальной раздачи.
    internal static bool IsAllowedRedirect(Uri from, Uri to)
    {
        if (to.Scheme == Uri.UriSchemeHttps)
        {
            return string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase);
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

    private static bool IsLoopback(Uri uri) =>
        uri.Host is "localhost" or "127.0.0.1" or "::1";

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
