using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace SCU.Tests;

// HTTPS-загрузчик пакетов базы (п. 30): сетевой http запрещён, loopback
// разрешён (тесты/локальная раздача), содержимое пишется во временный файл.
// Suite=Integration: нужны реальные loopback-соединения.
//
// Локальные серверы построены на TcpListener, а не HttpListener: http.sys
// требует URLACL-резервацию на префикс и недоступен в изолированных
// окружениях (агентские песочницы) — TcpListener работает везде.
[Trait("Suite", "Integration")]
public class ScannerUpdateServiceTests
{
    public ScannerUpdateServiceTests()
    {
        // Песочницы/ритуалы хост-провайдеров режут запись в %TEMP%; пакеты
        // кладём в тестовый workspace (boевой путь — %TEMP%).
        ScannerUpdateService.TempRootOverrideForTests = TestScratch.Root;
    }
    [Fact]
    public async Task DownloadPackage_RejectsRemoteHttp()
    {
        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync("http://example.com/database-latest.zip");

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Code);
    }

    [Fact]
    public async Task DownloadPackage_RejectsGarbageUrl()
    {
        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync("not a url");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task DownloadPackage_DownloadsFromLoopback()
    {
        const string packageBytes = "PK-scu-test-package";
        using var server = LoopbackHttpServer.Content(packageBytes);

        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync(server.Url);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(packageBytes, await File.ReadAllTextAsync(result.Value!));
        File.Delete(result.Value!);
    }

    // П. 8 аудита: redirect обрабатывается вручную — только HTTPS/loopback,
    // без смены хоста, без понижения схемы, с лимитом количества переходов.
    // URL-override из %APPDATA% ограничен доверенными хостами: иначе процесс
    // того же пользователя уводил бы автообновление базы на свой сервер.
    [Fact]
    public void OverridePolicy_GithubAndLoopbackAllowed()
    {
        Assert.True(ScannerUpdateService.IsAllowedOverride(
            "https://github.com/endlessbum/SCU/releases/download/scanner-db/database-latest.zip"));
        Assert.True(ScannerUpdateService.IsAllowedOverride(ScannerUpdateService.DefaultUrl));
        Assert.True(ScannerUpdateService.IsAllowedOverride(
            "https://release-assets.githubusercontent.com/anything/database-latest.zip"));
        Assert.True(ScannerUpdateService.IsAllowedOverride("http://127.0.0.1:9001/db.zip"));
        Assert.True(ScannerUpdateService.IsAllowedOverride("http://localhost/db.zip"));
    }

    [Fact]
    public void OverridePolicy_ForeignHostsRefused()
    {
        Assert.False(ScannerUpdateService.IsAllowedOverride("https://evil.example/db.zip"));
        Assert.False(ScannerUpdateService.IsAllowedOverride("https://github.com.evil.example/db.zip"));
        Assert.False(ScannerUpdateService.IsAllowedOverride("ftp://github.com/db.zip"));
        Assert.False(ScannerUpdateService.IsAllowedOverride("not a url"));
        Assert.False(ScannerUpdateService.IsAllowedOverride(""));
    }

    [Fact]
    public void RedirectPolicy_NoDowngradeNoCrossHost()
    {
        Assert.True(ScannerUpdateService.IsAllowedRedirect(
            new Uri("http://127.0.0.1:9001/a"), new Uri("http://127.0.0.1:9002/b")));
        Assert.True(ScannerUpdateService.IsAllowedRedirect(
            new Uri("http://127.0.0.1:9001/a"), new Uri("https://127.0.0.1/b")));
        Assert.False(ScannerUpdateService.IsAllowedRedirect(
            new Uri("https://updates.scu.example/db.zip"), new Uri("http://updates.scu.example/db.zip")));
        Assert.False(ScannerUpdateService.IsAllowedRedirect(
            new Uri("https://updates.scu.example/db.zip"), new Uri("https://evil.example/db.zip")));
        Assert.False(ScannerUpdateService.IsAllowedRedirect(
            new Uri("http://127.0.0.1:9001/a"), new Uri("http://localhost:9001/b")));
    }

    [Fact]
    public async Task DownloadPackage_FollowsAllowedLoopbackRedirect()
    {
        const string packageBytes = "PK-scu-redirected-package";
        using var target = LoopbackHttpServer.Content(packageBytes);
        var port = GetFreeTestPort();
        using var source = LoopbackHttpServer.Redirect($"http://127.0.0.1:{port}/package.zip", target.Url);

        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync(source.Url);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(packageBytes, await File.ReadAllTextAsync(result.Value!));
        File.Delete(result.Value!);
    }

    [Fact]
    public async Task DownloadPackage_RejectsCrossHostRedirect()
    {
        using var target = LoopbackHttpServer.Content("PK-secret");
        var port = GetFreeTestPort();
        // 127.0.0.1 -> localhost: другой хост — переход запрещён политикой.
        using var source = LoopbackHttpServer.Redirect(
            $"http://127.0.0.1:{port}/package.zip",
            target.Url.Replace("127.0.0.1", "localhost"));

        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync(source.Url);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task DownloadPackage_RejectsRedirectLoop()
    {
        var port = GetFreeTestPort();
        var selfUrl = $"http://127.0.0.1:{port}/package.zip";
        using var source = LoopbackHttpServer.Redirect(selfUrl, selfUrl);

        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync(source.Url);

        Assert.False(result.IsSuccess);
        Assert.Contains("перенаправлен", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // П. 11 аудита (partial download): обрыв соединения в середине тела ответа —
    // это провал загрузки, а не «успешно скачанный» урезанный пакет.
    [Fact]
    public async Task DownloadPackage_TruncatedBody_IsRejected()
    {
        const string packageBytes = "PK-scu-truncated-package-that-ends-early";
        var declaredLength = Encoding.UTF8.GetByteCount(packageBytes) + 4096;
        using var server = LoopbackHttpServer.Truncated(packageBytes, declaredLength);

        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync(server.Url);

        Assert.False(result.IsSuccess);
    }

    private static int GetFreeTestPort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    // Минимальный локальный HTTP-сервер на TcpListener (loopback). Обрабатывает
    // подключения последовательно до Dispose: достаточно для синхронного
    // сценария «запрос → одна-две переадресации → тело».
    private sealed class LoopbackHttpServer : IDisposable
    {
        private enum Mode { Body, Redirect, Truncated }

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Mode _mode;
        private readonly byte[] _body;
        private readonly long? _declaredLength;
        private readonly string? _location;

        private LoopbackHttpServer(Mode mode, byte[] body, long? declaredLength, string? location, int? port)
        {
            _mode = mode;
            _body = body;
            _declaredLength = declaredLength;
            _location = location;
            _listener = new TcpListener(IPAddress.Loopback, port ?? 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/package.zip";
            _ = Task.Run(AcceptLoopAsync);
        }

        public static LoopbackHttpServer Content(string body) =>
            new(Mode.Body, Encoding.UTF8.GetBytes(body), null, null, null);

        public static LoopbackHttpServer Truncated(string body, long declaredLength) =>
            new(Mode.Truncated, Encoding.UTF8.GetBytes(body), declaredLength, null, null);

        public static LoopbackHttpServer Redirect(string url, string location) =>
            new(Mode.Redirect, [], null, location, new Uri(url).Port) { Url = url };

        public string Url { get; private set; } = string.Empty;

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptSocketAsync(_stop.Token);
                }
                catch (Exception)
                {
                    // listener остановлен (Dispose) — конец цикла.
                    return;
                }

                using (client)
                {
                    try
                    {
                        await RespondAsync(client);
                    }
                    catch (Exception)
                    {
                        // Клиент мог прервать соединение — не важно для теста.
                    }
                }
            }
        }

        private async Task RespondAsync(Socket client)
        {
            await using var stream = new NetworkStream(client, ownsSocket: false);
            var readBuffer = new byte[4096];
#pragma warning disable CA2022 // достаточно первого непустого чтения: уверяемся, что заголовок запроса дошёл
            await stream.ReadAsync(readBuffer, _stop.Token);
#pragma warning restore CA2022

            switch (_mode)
            {
                case Mode.Body:
                {
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " +
                        _body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, _stop.Token);
                    await stream.WriteAsync(_body, _stop.Token);
                    break;
                }
                case Mode.Redirect:
                {
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 302 Found\r\nLocation: " + _location +
                        "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, _stop.Token);
                    break;
                }
                case Mode.Truncated:
                {
                    // Content-Length обещает лишние байты, реально отдаём только
                    // тело и закрываем соединение: клиент должен сообщить о
                    // недокачанном пакете, а не принять урезанный файл.
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " +
                        _declaredLength!.Value + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, _stop.Token);
                    await stream.WriteAsync(_body, _stop.Token);
                    break;
                }
            }

            client.Shutdown(SocketShutdown.Send);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
