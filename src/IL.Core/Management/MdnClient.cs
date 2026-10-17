using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IL.Core.Infrastructure;
namespace IL.Core.Management;

/// <summary>Organization transport is independent of the optional Aliyun transport.</summary>
public sealed class MdnClient : IAsyncDisposable
{
    private readonly string _root, _version;
    private HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _request = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Channel<object> _events = Channel.CreateBounded<object>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
    private Task? _loop;
    private Task? _realtime;
    private ClientWebSocket? _socket;
    private string? _token;
    public MdnConfiguration? Configuration { get; private set; }
    public string DeviceId { get; }
    public ManagedPolicy Policy { get; private set; } = new();
    public string Status { get; private set; } = "未加入管理";
    public bool IsRealtime => _socket?.State == WebSocketState.Open;
    public int? LatencyMs { get; private set; }
    public DateTimeOffset? LastSynchronizedAt { get; private set; }
    public string? LastConnectionError { get; private set; }
    public bool PolicyReceiptConfirmed { get; private set; }
    public event Action? Changed;
    public string Banner => Configuration is null ? "" : $"当前由{Configuration.OrganizationName}管理 · {DeviceId.Split('-')[^1]}";
    public MdnClient(string root, string version)
    {
        _root = Path.Combine(root, "mdn"); _version = version; DeviceId = DeviceIdentity.GetOrCreate(root);
        try
        {
            Configuration = MdnConfiguration.Load(root);
            if (Configuration is null) return;
            _http.Dispose();_http=new(MdnConfiguration.CreateHttpHandler(Configuration.ServerCertificateSha256)){Timeout=TimeSpan.FromSeconds(10)};
            Status = "等待连接集控服务器";
            Directory.CreateDirectory(_root);
            var saved = Path.Combine(_root, "state.bin");
            if (File.Exists(saved))
            {
                var state = JsonSerializer.Deserialize<State>(WindowsSecretStore.Load(saved), ManagedPolicy.Json)!;
                if (state.Server == Configuration.Server.AbsoluteUri && state.DeviceId == DeviceId)
                { state.Policy.Validate(); _token = state.Token; Policy = state.Policy; }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { Status = "集控配置或缓存无效，请联系组织管理员"; }
    }
    private sealed record State(string Server, string DeviceId, string Token, ManagedPolicy Policy);
    private void Save() => WindowsSecretStore.Save(Path.Combine(_root, "state.bin"), JsonSerializer.Serialize(new State(Configuration!.Server.AbsoluteUri, DeviceId, _token!, Policy), ManagedPolicy.Json));
    public void Start()
    {
        if (Configuration is null || _loop is not null) return;
        _loop = LoopAsync(); _realtime = RealtimeLoopAsync();
    }
    public async Task JoinYamlAsync(string path, CancellationToken ct = default)
    {
        if (Configuration is not null) throw new InvalidOperationException("已加入组织管理，无法导入注册配置。");
        var config = MdnConfiguration.LoadFile(path);
        await JoinAsync(config.Server, new MdnServerInfo(config.OrganizationName, config.UpdatePublicKey, config.ServerCertificateSha256), config.EnrollmentToken, ct, config.PollSeconds);
    }
    public async Task JoinAsync(Uri server, MdnServerInfo identity, string enrollmentToken, CancellationToken ct = default, int pollSeconds = 60)
    {
        if (Configuration is not null) throw new InvalidOperationException("已加入管理；更换组织前需要管理员授权退出。");
        if (string.IsNullOrWhiteSpace(enrollmentToken)) throw new FormatException("请填写组织共享注册凭证。");
        var configuration = new MdnConfiguration
        {
            ServerUrl = server.AbsoluteUri, OrganizationName = identity.OrganizationName,
            EnrollmentToken = enrollmentToken.Trim(), UpdatePublicKey = identity.UpdatePublicKey,
            ServerCertificateSha256 = identity.CertificateSha256, DisableSsl = server.Scheme == "http", PollSeconds = Math.Clamp(pollSeconds, 15, 3600)
        };
        var handler = MdnConfiguration.CreateHttpHandler(configuration.ServerCertificateSha256);
        Directory.CreateDirectory(_root);
        var path = Path.Combine(Path.GetDirectoryName(_root)!, "il_mdn_config.yaml");
        var yaml = new YamlDotNet.Serialization.SerializerBuilder().WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance).Build().Serialize(configuration);
        File.WriteAllText(path + ".tmp", yaml); File.Move(path + ".tmp", path, true);
        _http.Dispose(); _http = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
        Configuration = configuration; Status = "正在连接集控服务器"; Changed?.Invoke();
        try { await SynchronizeAsync(ct); }
        catch
        {
            if (_token is null) { Configuration = null; File.Delete(path); }
            Status = "集控连接失败，请检查地址与注册凭证后重试"; Changed?.Invoke(); throw;
        }
        finally { Start(); }
    }
    public Task ChangeTransportAsync(bool disableSsl, string certificatePin = "", CancellationToken ct = default) =>
        Task.FromException(new InvalidOperationException(Configuration is null
            ? "请在加入管理时选择连接方式。"
            : "加入组织管理后，连接设置已锁定。请先由管理员授权退出管理。"));
    public async Task LeaveAsync(CancellationToken ct = default)
    {
        if (Configuration is null) return;
        await _request.WaitAsync(ct);
        try
        {
            if (_token is null) throw new InvalidOperationException("设备尚未注册，请先连接服务器。");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Configuration.Server, "api/leave"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException("退出尚未获得管理员授权，请联系组织管理员在后台允许此设备退出。");
            response.EnsureSuccessStatusCode();
            _stop.Cancel();
        }
        finally { _request.Release(); }
        if (_loop is not null) await _loop;
        if (_realtime is not null) await _realtime;
        File.Delete(Path.Combine(Path.GetDirectoryName(_root)!, "il_mdn_config.yaml"));
        foreach (var file in new[] { "state.bin", "events.bin" }) File.Delete(Path.Combine(_root, file));
        while (_events.Reader.TryRead(out _)) { }
        _stop.Dispose(); _stop = new(); _loop = null; _realtime = null; _token = null; Configuration = null;
        Policy = new(); LastSynchronizedAt = null; LastConnectionError = null; PolicyReceiptConfirmed = false; LatencyMs = null; Status = "未加入管理"; Changed?.Invoke();
    }
    public void Report(string kind, object metrics)
    {
        if (Configuration is not null) _events.Writer.TryWrite(new { id = Guid.NewGuid().ToString("N"), kind, occurredAt = DateTimeOffset.UtcNow, appVersion = _version, metrics });
    }
    private async Task LoopAsync()
    {
        Report("startup", new { platform = "Windows", architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() });
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_socket?.State != WebSocketState.Open) await SynchronizeAsync(_stop.Token, polling: true);
                else
                {
                    await _request.WaitAsync(_stop.Token);
                    try { await FlushEventsAsync(_stop.Token); await FlushCrashesAsync(_stop.Token); await FlushUpdateReceiptsAsync(_stop.Token); }
                    finally { _request.Release(); }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                AppLog.Warning("集控同步或反馈提交失败，将自动重试", e);
                if (_socket?.State != WebSocketState.Open) { Status = "集控连接失败，将自动重试"; Changed?.Invoke(); }
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(Configuration!.PollSeconds), _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    private async Task RealtimeLoopAsync()
    {
        var delay = 1;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_token is null) { await Task.Delay(1000, _stop.Token); continue; }
                var config = Configuration!;
                using var socket = new ClientWebSocket();
                using var transport = new HttpMessageInvoker(MdnConfiguration.CreateHttpHandler(config.ServerCertificateSha256));
                _socket = socket;
                socket.Options.SetRequestHeader("Authorization", "Bearer " + _token);
                var endpoint = new UriBuilder(new Uri(config.Server, "api/connect")) { Scheme = config.DisableSsl ? "ws" : "wss" }.Uri;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                { deadline.CancelAfter(TimeSpan.FromSeconds(10)); await socket.ConnectAsync(endpoint, transport, deadline.Token); }
                delay = 1; Status = "已实时连接集控服务器"; Changed?.Invoke();
                AppLog.Operation("mdn", "实时连接已建立，停止定时拉取策略");
                using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var heartbeat = HeartbeatAsync(socket, session.Token);
                try
                {
                    var buffer = new byte[4096];
                    while (socket.State == WebSocketState.Open)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(75));
                        var count = 0;
                        ValueWebSocketReceiveResult message;
                        do
                        {
                            message = await socket.ReceiveAsync(buffer.AsMemory(count), timeout.Token);
                            count += message.Count;
                            if (message.MessageType == WebSocketMessageType.Close) break;
                            if (message.MessageType != WebSocketMessageType.Text || count == buffer.Length && !message.EndOfMessage)
                                throw new FormatException("实时消息无效。");
                        } while (!message.EndOfMessage);
                        if (message.MessageType == WebSocketMessageType.Close) break;
                        using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
                        var kind = json.RootElement.GetProperty("type").GetString();
                        if (kind is "ready" or "policy") await SynchronizeAsync(session.Token);
                        else if (kind == "heartbeat" && json.RootElement.TryGetProperty("sentAt", out var sent) && sent.TryGetInt64(out var stamp))
                        {
                            var latency = (int)Math.Clamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp, 0, 60000);
                            if (json.RootElement.TryGetProperty("revision", out var revision) && revision.TryGetInt64(out var latest) && latest > Policy.Revision)
                            { AppLog.Operation("mdn", $"心跳发现待同步策略 revision={latest}"); await SynchronizeAsync(session.Token); }
                            LatencyMs = latency; Changed?.Invoke();
                            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(config.Server, "api/connection"));
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                            request.Content = JsonContent.Create(new { latency });
                            using var response = await _http.SendAsync(request, session.Token);
                        }
                    }
                }
                finally { session.Cancel(); socket.Abort(); try { await heartbeat; } catch (OperationCanceledException) { } catch (WebSocketException) { } }
                if (!_stop.IsCancellationRequested) { Status = "实时连接断开，将自动重连并轮询配置"; Changed?.Invoke(); }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is not OutOfMemoryException) { Status = "实时连接断开，将自动重连并轮询配置"; Changed?.Invoke(); }
            finally { _socket = null; if (!_stop.IsCancellationRequested) { AppLog.Operation("mdn", "实时连接断开，恢复策略轮询"); if (_wake.CurrentCount == 0) _wake.Release(); } }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), _stop.Token); } catch (OperationCanceledException) { break; }
            delay = Math.Min(30, delay * 2);
        }
    }
    private static async Task HeartbeatAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "heartbeat", sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }), WebSocketMessageType.Text, true, ct);
            await Task.Delay(TimeSpan.FromSeconds(25), ct);
        }
    }
    public string? DownloadCredential(Uri target) => Configuration is {} config && target.Scheme == config.Server.Scheme && target.Authority == config.Server.Authority ? _token : null;
    public async Task SynchronizeAsync(CancellationToken ct = default, bool polling = false)
    {
        if (Configuration is null) return;
        await _request.WaitAsync(ct);
        try
        {
            if (polling && _socket?.State == WebSocketState.Open) return;
            if (_token is null)
            {
                using var registration = await _http.PostAsJsonAsync(new Uri(Configuration.Server, "api/register"), new { deviceId = DeviceId, deviceName = Environment.MachineName, appVersion = _version, enrollmentToken = Configuration.EnrollmentToken }, ManagedPolicy.Json, ct);
                registration.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await registration.Content.ReadAsStringAsync(ct));
                _token = json.RootElement.GetProperty("deviceToken").GetString() ?? throw new FormatException(); Save();
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Configuration.Server, "api/policy"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-IL-Capabilities", "transcriptBlurIntensity,lessonDistribution");
            using var response = await _http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            var policy = await response.Content.ReadFromJsonAsync<ManagedPolicy>(ManagedPolicy.Json, ct) ?? throw new FormatException(); policy.Validate();
            var different = JsonSerializer.Serialize(Policy, ManagedPolicy.Json) != JsonSerializer.Serialize(policy, ManagedPolicy.Json);
            Policy = policy; Save(); LastSynchronizedAt = DateTimeOffset.Now; LastConnectionError = null; PolicyReceiptConfirmed = false; Status = _socket?.State == WebSocketState.Open ? "已实时连接集控服务器" : "已连接集控服务器（轮询）"; if (different) Changed?.Invoke();
            AppLog.Operation("mdn", $"策略同步完成 revision={policy.Revision} trigger={(polling ? "poll" : "push/manual")}");
            // Acknowledge only validated, persisted policy, independently from update execution.
            try
            {
                using var receipt = new HttpRequestMessage(HttpMethod.Post, new Uri(Configuration.Server, "api/policy/receipt"));
                receipt.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                receipt.Content = JsonContent.Create(new { revision = policy.Revision });
                using var accepted = await _http.SendAsync(receipt, ct);
                PolicyReceiptConfirmed = accepted.IsSuccessStatusCode;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            { AppLog.Debug("策略回执稍后重试：" + error.GetBaseException().Message); }
            Report("online", new { });
            await FlushEventsAsync(ct);
            await FlushCrashesAsync(ct); Changed?.Invoke();
            await FlushUpdateReceiptsAsync(ct);
        }
        catch (Exception error)
        {
            LastConnectionError = CrashReportStore.Redact(error.ToString()); Changed?.Invoke();
            AppLog.Warning("集控策略同步失败", error); throw;
        }
        finally { _request.Release(); }
    }
    public async Task<bool> ReportLessonAsync(string id, string status, string message, CancellationToken ct)
    {
        await _request.WaitAsync(ct);
        try
        {
            if (Configuration is null || _token is null) return false;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Configuration.Server, "api/lessons/" + Guid.Parse(id).ToString("D") + "/receipt"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = JsonContent.Create(new { status, message }, options: ManagedPolicy.Json);
            using var response = await _http.SendAsync(request, ct); return response.IsSuccessStatusCode;
        }
        finally { _request.Release(); }
    }
    private readonly SemaphoreSlim _feedbackGate = new(1, 1);
    private string FeedbackPath => Path.Combine(_root, "update-receipts.bin");
    public async Task ReportUpdateAsync(string id, string status, string message = "", int progress = 0)
    {
        await _feedbackGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_root);
            var pending = File.Exists(FeedbackPath) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(WindowsSecretStore.Load(FeedbackPath)) ?? new() : new Dictionary<string, JsonElement>();
            pending[Guid.Parse(id).ToString("D")] = JsonSerializer.SerializeToElement(new { status, message, progress });
            WindowsSecretStore.Save(FeedbackPath, JsonSerializer.Serialize(pending));
        }
        finally { _feedbackGate.Release(); }
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await FlushUpdateReceiptsAsync(timeout.Token); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
    }
    private async Task FlushUpdateReceiptsAsync(CancellationToken ct)
    {
        if (Configuration is null || _token is null) return;
        await _feedbackGate.WaitAsync(ct);
        try
        {
            if (!File.Exists(FeedbackPath)) return;
            var pending = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(WindowsSecretStore.Load(FeedbackPath)) ?? new();
            foreach (var pair in pending.ToArray())
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Configuration.Server, "api/updates/" + pair.Key + "/receipt"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Content = JsonContent.Create(pair.Value);
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound) break;
                pending.Remove(pair.Key); WindowsSecretStore.Save(FeedbackPath, JsonSerializer.Serialize(pending));
            }
        }
        finally { _feedbackGate.Release(); }
    }
    private async Task<bool> SendEventAsync(object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Configuration!.Server, "api/telemetry"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = JsonContent.Create(payload, options: ManagedPolicy.Json);
        using var response = await _http.SendAsync(request, ct); return response.IsSuccessStatusCode;
    }
    private async Task FlushEventsAsync(CancellationToken ct)
    {
        // Keep an undelivered batch encrypted across restarts, with a bounded queue.
        var path = Path.Combine(_root, "events.bin");
        var pending = new List<JsonElement>();
        if (File.Exists(path)) pending.AddRange(JsonSerializer.Deserialize<List<JsonElement>>(WindowsSecretStore.Load(path)) ?? []);
        while (_events.Reader.TryRead(out var item)) pending.Add(JsonSerializer.SerializeToElement(item, ManagedPolicy.Json));
        pending = pending.TakeLast(128).ToList();
        WindowsSecretStore.Save(path, JsonSerializer.Serialize(pending));
        while (_token is not null && pending.Count > 0)
        {
            if (!await SendEventAsync(pending[0], ct)) break;
            pending.RemoveAt(0); WindowsSecretStore.Save(path, JsonSerializer.Serialize(pending));
        }
    }
    private async Task FlushCrashesAsync(CancellationToken ct)
    {
        var store = new CrashReportStore(Path.GetDirectoryName(_root)!);
        var markers = Path.Combine(_root, "crashes"); Directory.CreateDirectory(markers);
        foreach (var report in store.Reports().TakeLast(100))
        {
            var marker = Path.Combine(markers, report.Id + ".full.sent"); if (File.Exists(marker)) continue;
            if (!await SendEventAsync(new { id = "error-" + report.Id, kind = "error_track", occurredAt = report.OccurredAt, appVersion = report.AppVersion,
                metrics = new { reportId = report.Id, report.ExceptionType, report.Component, report.Fatal, report.ExitCode, report.Platform, report.Architecture, report.ProcessId,
                    message = CrashReportStore.Redact(report.Message), details = CrashReportStore.Redact(report.Details), recentOperations = CrashReportStore.Redact(report.RecentOperations) } }, ct)) break;
            File.WriteAllText(marker, "sent");
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); if (_loop is not null) await _loop;
        if (_realtime is not null) await _realtime;
        if (Configuration is not null) { try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await FlushEventsAsync(timeout.Token); } catch (Exception) { } }
        _http.Dispose(); _stop.Dispose();
    }
}
