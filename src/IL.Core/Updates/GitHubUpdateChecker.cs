using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace IL.Core.Updates;

public sealed record UpdateCheckResult(string State, string Message, string? Version = null, string? ReleaseUrl = null);
public sealed class GitHubUpdateChecker(HttpMessageHandler? handler = null) : IDisposable
{
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
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/luyii-code-1/Intensive_Listening/releases/latest");
            request.Headers.UserAgent.ParseAdd("IntensiveListening/" + current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _http.SendAsync(request, timeout.Token); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token)); var release = json.RootElement;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!TryVersion(tag, out var latest) || !TryVersion(current, out var installed)) throw new FormatException("Latest 版本号无法识别。");
            var url = release.GetProperty("html_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var link) || link.Scheme != "https" || link.Host != "github.com" ||
                !link.AbsolutePath.StartsWith("/luyii-code-1/Intensive_Listening/releases/", StringComparison.Ordinal)) throw new FormatException("Release 链接无效。");
            Set(latest > installed ? new("available", $"发现新版本 {tag}", tag, url) : new("current", "当前已是最新版本", tag, url));
        }
        catch (OperationCanceledException) { Set(new("failed", cancellation.IsCancellationRequested ? "更新检查已取消" : "检查更新失败：连接超过 10 秒")); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        { Set(new("failed", "检查更新失败，请稍后重试")); }
        finally { _gate.Release(); }
    }
    public static bool TryVersion(string value, out Version version)
    {
        value = Regex.Replace(value, @"\((\d+)\)", ".$1");
        var match = Regex.Match(value, @"^v?(\d+\.\d+\.\d+(?:\.\d+)?)(?:[-+].*)?$", RegexOptions.IgnoreCase);
        version = new Version(0, 0, 0); if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed)) return false;
        version = parsed; return true;
    }
    private void Set(UpdateCheckResult result) { Result = result; Changed?.Invoke(); }
    public void Dispose() => _http.Dispose();
}
