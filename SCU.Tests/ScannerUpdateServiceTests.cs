using System.Net;
using SCU.Services;
using Xunit;

namespace SCU.Tests;

// HTTPS-загрузчик пакетов базы (п. 30): сетевой http запрещён, loopback
// разрешён (тесты/локальная раздача), содержимое пишется во временный файл.
public class ScannerUpdateServiceTests
{
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
        using var server = new LocalHttpServer(packageBytes);

        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync(server.Url);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(packageBytes, await File.ReadAllTextAsync(result.Value!));
        File.Delete(result.Value!);
    }

    // Минимальный локальный сервер для теста загрузки (loopback).
    private sealed class LocalHttpServer : IDisposable
    {
        private readonly HttpListener _listener;

        public LocalHttpServer(string content)
        {
            Content = content;
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/package.zip";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(ServeLoop);
        }

        public string Url { get; }

        private string Content { get; }

        private async Task ServeLoop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var buffer = System.Text.Encoding.UTF8.GetBytes(Content);
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer);
                context.Response.Close();
            }
        }

        private static int GetFreePort()
        {
            var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            socket.Start();
            var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    // П. 8 аудита: redirect обрабатывается вручную — только HTTPS/loopback,
    // без смены хоста, без понижения схемы, с лимитом количества переходов.
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
        using var target = new LocalHttpServer(packageBytes);
        using var source = new RedirectHttpServer($"http://127.0.0.1:{GetFreeTestPort()}/package.zip", target.Url);

        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync(source.Url);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(packageBytes, await File.ReadAllTextAsync(result.Value!));
        File.Delete(result.Value!);
    }

    [Fact]
    public async Task DownloadPackage_RejectsCrossHostRedirect()
    {
        using var target = new LocalHttpServer("PK-secret");
        // 127.0.0.1 -> localhost: другой хост — переход запрещён политикой.
        using var source = new RedirectHttpServer($"http://127.0.0.1:{GetFreeTestPort()}/package.zip", target.Url.Replace("127.0.0.1", "localhost"));

        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync(source.Url);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task DownloadPackage_RejectsRedirectLoop()
    {
        var selfUrl = $"http://127.0.0.1:{GetFreeTestPort()}/package.zip";
        using var source = new RedirectHttpServer(selfUrl, selfUrl);

        var service = new ScannerUpdateService();
        var result = await service.DownloadPackageAsync(source.Url);

        Assert.False(result.IsSuccess);
        Assert.Contains("перенаправлен", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetFreeTestPort()
    {
        var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    // Локальный сервер, всегда отвечающий 302 на заданный Location.
    private sealed class RedirectHttpServer : IDisposable
    {
        private readonly HttpListener _listener;

        public RedirectHttpServer(string url, string location)
        {
            Url = url;
            _listener = new HttpListener();
            _listener.Prefixes.Add(url[..url.LastIndexOf('/')] + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    context.Response.Redirect(location);
                    context.Response.Close();
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
