using System.Net.Http.Headers;
using System.Text.Json;
namespace IL.Core.Updates;

public sealed record UpdateCheckResult(string State, string Message, string? Version = null, string? ReleaseUrl = null);
public sealed class WebsiteUpdateChecker(HttpMessageHandler? handler = null) : IDisposable
{
    public const string MetadataUrl = "https://il.luyii.cn/downloads/latest.json";
    public const string DownloadPageUrl = "https://il.luyii.cn/#download";
    private readonly HttpClient _http = handler is null ? new() : new(handler);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public UpdateCheckResult Result { get; private set; } = new("idle", "尚未检查更新");
    public event Action? Changed;
    public async Task CheckAsync(string current, CancellationToken cancellation = default)
    {
        if (!await _gate.WaitAsync(0, cancellation)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            Set(new("checking", "正在检查更新…"));
            using var request = new HttpRequestMessage(HttpMethod.Get, MetadataUrl);
            request.Headers.UserAgent.ParseAdd("IntensiveListening/" + current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await _http.SendAsync(request, timeout.Token); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token)); var release = json.RootElement;
            var tag = release.GetProperty("tag").GetString() ?? "";
            if (!ManagedVersion.TryParse(tag, out var latest) || latest.Channel != 2 || !ManagedVersion.TryParse(current, out var installed))
                throw new FormatException("官网版本号无法识别。");
            Set(latest.CompareTo(installed) > 0
                ? new("available", $"发现新版本 {tag.TrimStart('v', 'V')}", tag, DownloadPageUrl)
                : new("current", "当前已是最新版本", tag, DownloadPageUrl));
        }
        catch (OperationCanceledException) { Set(new("failed", cancellation.IsCancellationRequested ? "更新检查已取消" : "检查更新失败：连接超过 10 秒")); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        { Set(new("failed", "检查更新失败，请稍后重试")); }
        finally { _gate.Release(); }
    }
    private void Set(UpdateCheckResult result) { Result = result; Changed?.Invoke(); }
    public void Dispose() => _http.Dispose();
}
