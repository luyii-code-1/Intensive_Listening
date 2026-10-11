using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using IL.Core.Infrastructure;
namespace IL.Core.Management;

/// <summary>Organization transport is independent of the optional Aliyun transport.</summary>
public sealed class MdnClient : IAsyncDisposable
{
    private readonly string _root, _version;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _request = new(1, 1);
    private readonly Channel<object> _events = Channel.CreateBounded<object>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
    private Task? _loop;
    private string? _token;
    public MdnConfiguration? Configuration { get; }
    public string DeviceId { get; }
    public ManagedPolicy Policy { get; private set; } = new();
    public string Status { get; private set; } = "未加入管理";
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
    public void Start() { if (Configuration is not null && _loop is null) _loop = LoopAsync(); }
    public void Report(string kind, object metrics)
    {
        if (Configuration is not null) _events.Writer.TryWrite(new { id = Guid.NewGuid().ToString("N"), kind, occurredAt = DateTimeOffset.UtcNow, appVersion = _version, metrics });
    }
    private async Task LoopAsync()
    {
        Report("startup", new { platform = "Windows", architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() });
        while (!_stop.IsCancellationRequested)
        {
            try { await SynchronizeAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is not OutOfMemoryException) { Status = "集控连接失败，将自动重试"; Changed?.Invoke(); }
            try { await Task.Delay(TimeSpan.FromSeconds(Configuration!.PollSeconds), _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    public async Task SynchronizeAsync(CancellationToken ct = default)
    {
        if (Configuration is null) return;
        await _request.WaitAsync(ct);
        try
        {
            if (_token is null)
            {
                using var registration = await _http.PostAsJsonAsync(new Uri(Configuration.Server, "api/register"), new { deviceId = DeviceId, deviceName = Environment.MachineName, appVersion = _version, enrollmentToken = Configuration.EnrollmentToken }, ManagedPolicy.Json, ct);
                registration.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await registration.Content.ReadAsStringAsync(ct));
                _token = json.RootElement.GetProperty("deviceToken").GetString() ?? throw new FormatException(); Save();
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Configuration.Server, "api/policy"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await _http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            var policy = await response.Content.ReadFromJsonAsync<ManagedPolicy>(ManagedPolicy.Json, ct) ?? throw new FormatException(); policy.Validate();
            var different = JsonSerializer.Serialize(Policy, ManagedPolicy.Json) != JsonSerializer.Serialize(policy, ManagedPolicy.Json);
            Policy = policy; Save(); Status = "已连接集控服务器"; if (different) Changed?.Invoke();
            Report("online", new { });
            await FlushEventsAsync(ct);
            await FlushCrashesAsync(ct); Changed?.Invoke();
        }
        finally { _request.Release(); }
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
            var marker = Path.Combine(markers, report.Id + ".sent"); if (File.Exists(marker)) continue;
            // Only structural diagnostics: exception messages can contain lesson text and secrets.
            var frames = report.Details.Split('\n').Where(x => x.TrimStart().StartsWith("at ", StringComparison.Ordinal)).Select(x => x.Split(" in ")[0].Trim()).Take(40).ToArray();
            if (!await SendEventAsync(new { id = "crash-" + report.Id, kind = "crash", occurredAt = report.OccurredAt, appVersion = report.AppVersion, metrics = new { report.ExceptionType, report.Component, report.Fatal, report.ExitCode, stack = frames } }, ct)) break;
            File.WriteAllText(marker, "sent");
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); if (_loop is not null) await _loop;
        if (Configuration is not null) { try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await FlushEventsAsync(timeout.Token); } catch (Exception) { } }
        _http.Dispose(); _stop.Dispose();
    }
}
