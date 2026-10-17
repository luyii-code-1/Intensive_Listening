using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IL.Core.Infrastructure;
using IL.Core.Management;
using Xunit;

namespace IL.Core.Tests;

public sealed class MdnRealtimeTests
{
    [Fact]
    public async Task LiveConnectionStopsPolicyPollingHandlesPushAndFallsBackAfterDisconnect()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-realtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var server = new Peer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "il_mdn_config.yaml"), $"schema_version: 1\norganization_name: Test\nserver_url: {server.Url}\nenrollment_token: test-token\ndisable_ssl: true\npoll_seconds: 15\n");
            var report = new CrashReport { Details = "nested exception\n" + new string('x', 20000), Message = "complete failure", RecentOperations = "open queue\nclose queue" };
            new CrashReportStore(root).Save(report);
            await using var client = new MdnClient(root, "2.1.0.1");
            client.Start();
            await Until(() => client.Status.Contains("实时") && server.PolicyRequests >= 2, deadline.Token);
            // The heartbeat is processed after the ready message and its initial policy sync.
            await server.HeartbeatAsync(deadline.Token);
            await Until(() => client.LatencyMs.HasValue, deadline.Token);
            var requests = server.PolicyRequests;
            await client.SynchronizeAsync(deadline.Token, polling: true);
            Assert.Equal(requests, server.PolicyRequests);
            await Task.Delay(15500, deadline.Token);
            Assert.Equal(requests, server.PolicyRequests);
            var sent = Assert.Single(server.Events, e => e.GetProperty("kind").GetString() == "error_track");
            Assert.Equal(report.Details, sent.GetProperty("metrics").GetProperty("details").GetString());
            Assert.Equal(report.RecentOperations, sent.GetProperty("metrics").GetProperty("recentOperations").GetString());
            server.Revision = 2;
            await server.HeartbeatAsync(deadline.Token);
            await Until(() => client.Policy.Revision == 2, deadline.Token);
            server.Revision = 3;
            await server.PushAsync(deadline.Token);
            await Until(() => client.Policy.Revision == 3, deadline.Token);
            var beforeDisconnect = server.PolicyRequests;
            server.Disconnect();
            await Until(() => server.PolicyRequests > beforeDisconnect && client.Status.Contains("轮询"), deadline.Token);
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task Until(Func<bool> condition, CancellationToken ct)
    { while (!condition()) await Task.Delay(20, ct); }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private readonly ConcurrentBag<Task> _connections = [];
        private WebSocket? _socket;
        private int _requests;
        private bool _disconnected;
        public long Revision = 1;
        public int PolicyRequests => Volatile.Read(ref _requests);
        public ConcurrentQueue<JsonElement> Events { get; } = new();
        public string Url { get; }
        public Peer()
        {
            _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) { var connection = await _listener.AcceptTcpClientAsync(_stop.Token); _connections.Add(HandleAsync(connection)); } }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient connection)
        {
            using (connection)
            {
                try
                {
                    var stream = connection.GetStream(); var header = new StringBuilder(); var next = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    { if (await stream.ReadAsync(next, _stop.Token) == 0) return; header.Append((char)next[0]); }
                    var lines = header.ToString().Split("\r\n"); var path = lines[0].Split(' ')[1];
                    var headers = lines.Skip(1).Where(line => line.Contains(':')).Select(line => line.Split(':', 2)).ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    if (path == "/api/connect" && !_disconnected)
                    {
                        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
                        using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan); _socket = socket;
                        await SendAsync(socket, "{\"type\":\"ready\"}", _stop.Token);
                        var buffer = new byte[4096];
                        while (!_stop.IsCancellationRequested)
                        {
                            var received = await socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                            if (received.MessageType == WebSocketMessageType.Close) return;
                        }
                    }
                    else
                    {
                        var size = headers.TryGetValue("Content-Length", out var length) ? int.Parse(length) : 0;
                        var body = new byte[size]; if (size > 0) await stream.ReadExactlyAsync(body, _stop.Token);
                        if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding == "chunked")
                        {
                            using var chunks = new MemoryStream();
                            while (true)
                            {
                                var line = new StringBuilder();
                                while (true) { await stream.ReadExactlyAsync(next, _stop.Token); if (next[0] == '\n') break; if (next[0] != '\r') line.Append((char)next[0]); }
                                var chunkSize = Convert.ToInt32(line.ToString().Split(';')[0], 16);
                                var chunk = new byte[chunkSize];
                                if (chunkSize > 0) { await stream.ReadExactlyAsync(chunk, _stop.Token); chunks.Write(chunk); }
                                var end = new byte[2]; await stream.ReadExactlyAsync(end, _stop.Token);
                                if (chunkSize == 0) break;
                            }
                            body = chunks.ToArray();
                        }
                        string json = "{}";
                        if (path == "/api/register") json = "{\"deviceToken\":\"device-token\"}";
                        else if (path == "/api/policy") { Interlocked.Increment(ref _requests); json = $"{{\"revision\":{Revision}}}"; }
                        else if (path == "/api/telemetry") { using var document = JsonDocument.Parse(body); Events.Enqueue(document.RootElement.Clone()); }
                        var bytes = Encoding.UTF8.GetBytes(json);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                        await stream.WriteAsync(bytes, _stop.Token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or WebSocketException) { }
            }
        }
        private static Task SendAsync(WebSocket socket, string text, CancellationToken ct) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        public Task PushAsync(CancellationToken ct) => SendAsync(_socket!, "{\"type\":\"policy\"}", ct);
        public Task HeartbeatAsync(CancellationToken ct) => SendAsync(_socket!, JsonSerializer.Serialize(new { type = "heartbeat", sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), revision = Revision }), ct);
        public void Disconnect() { _disconnected = true; _socket?.Abort(); }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _socket?.Abort(); _listener.Stop(); await _accept; await Task.WhenAll(_connections); _stop.Dispose();
        }
    }
}
