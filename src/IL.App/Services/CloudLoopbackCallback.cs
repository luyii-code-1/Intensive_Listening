using System.Net;
using System.Net.Sockets;
using System.Text;
using IL.Core.Cloud;

namespace IL.App.Services;

public sealed class CloudLoopbackCallback : IAsyncDisposable
{
    private readonly TcpListener _listener;
    public CloudAuthorizationRequest Request { get; }

    public CloudLoopbackCallback()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(4);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Request = new(new Uri($"http://127.0.0.1:{port}/callback"));
    }

    public async Task<Uri> WaitAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(deadline.Token);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var stream = client.GetStream();
            try
            {
                var header = new byte[8192];
                var count = 0;
                while (count < header.Length)
                {
                    var received = await stream.ReadAsync(header.AsMemory(count, 1), requestTimeout.Token);
                    if (received == 0) break;
                    count += received;
                    if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header, 0, count).Split("\r\n");
                var first = lines[0].Split(' ');
                var hosts = lines.Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (count == header.Length || first.Length != 3 || first[0] != "GET" || first[2] is not ("HTTP/1.1" or "HTTP/1.0")
                    || !first[1].StartsWith("/callback?", StringComparison.Ordinal) || hosts.Length != 1
                    || hosts[0][5..].Trim() != Request.RedirectUri.Authority)
                { await RespondAsync(stream, false, requestTimeout.Token); continue; }
                var callback = new Uri(Request.RedirectUri.GetLeftPart(UriPartial.Authority) + first[1]);
                try { Request.ReadCode(callback); }
                catch (CloudAuthenticationException error) when (error.Code == "access_denied") { }
                catch (CloudAuthenticationException) { await RespondAsync(stream, false, requestTimeout.Token); continue; }
                await RespondAsync(stream, true, requestTimeout.Token);
                return callback;
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    private static Task RespondAsync(NetworkStream stream, bool accepted, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>Intensive Listening 云服务</title><p>"
            + (accepted ? "授权结果已收到，请返回 Intensive Listening。" : "授权回调无效，请返回应用重新登录。") + "</p></html>");
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\n\r\n");
        return stream.WriteAsync(headers.Concat(body).ToArray(), ct).AsTask();
    }

    public ValueTask DisposeAsync() { _listener.Stop(); return ValueTask.CompletedTask; }
}
