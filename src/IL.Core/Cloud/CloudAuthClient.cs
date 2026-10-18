using System.Net;
using System.Text.Json;

namespace IL.Core.Cloud;

public interface ICloudTokenStore
{
    Task<string?> ReadAsync(CancellationToken ct = default);
    Task WriteAsync(string refreshToken, CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default);
}

public sealed class CloudAuthClient : IDisposable
{
    public const string ClientId = "il-desktop";
    public const string Scope = "cloud.library cloud.projects cloud.asr";
    public static Uri Server { get; } = new("https://cloud.il.luyii.cn/");
    private readonly ICloudTokenStore _store;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken, _refreshToken;
    private DateTimeOffset _expiresAt;
    private bool _loaded;
    public bool HasCredentials { get; private set; }
    public string? DeviceId { get; private set; }

    public CloudAuthClient(ICloudTokenStore store, HttpClient? http = null)
    {
        _store = store;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await LoadCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task EnsureAuthorizationAvailableAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(new Uri(Server, "api/v1/auth/desktop-client"), ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
            throw AuthorizationUnavailable();
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var body = json.RootElement;
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("clientId", out var id) || id.GetString() != ClientId ||
                !body.TryGetProperty("protocolVersion", out var version) || !version.TryGetInt32(out var number) || number != 1 ||
                !body.TryGetProperty("authorizationEndpoint", out var authorize) || authorize.GetString() != "/connect/authorize" ||
                !body.TryGetProperty("tokenEndpoint", out var token) || token.GetString() != "/connect/token" ||
                !body.TryGetProperty("codeChallengeMethods", out var methods) || methods.ValueKind != JsonValueKind.Array ||
                !methods.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "S256")) throw AuthorizationUnavailable();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw AuthorizationUnavailable(); }
    }

    private static CloudAuthenticationException AuthorizationUnavailable() => new("authorization_unavailable", "客户端登录服务暂未就绪，请稍后重试。");

    public async Task CompleteLoginAsync(CloudAuthorizationRequest request, Uri callback, CancellationToken ct = default)
    {
        var code = request.ReadCode(callback);
        await _gate.WaitAsync(ct);
        try
        {
            using var response = await _http.PostAsync(new Uri(Server, "connect/token"), new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = ClientId,
                    ["code"] = code, ["redirect_uri"] = request.RedirectUri.AbsoluteUri, ["code_verifier"] = request.Verifier }), ct);
            await AcceptTokenAsync(response, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromSeconds(30)) return _accessToken;
            if (_refreshToken is null) throw new CloudAuthenticationException("login_required", "请先登录 Intensive Listening 云服务。");
            try
            {
                using var response = await _http.PostAsync(new Uri(Server, "connect/token"), new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = _refreshToken }), ct);
                await AcceptTokenAsync(response, ct);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException
                || error is CloudAuthenticationException { Code: "invalid_token_response" })
            {
                // The server may have rotated the token even when its response was lost.
                await ClearCoreAsync();
                throw new CloudAuthenticationException("login_required", "登录状态更新未完成，请重新授权 Intensive Listening 云服务。");
            }
            return _accessToken!;
        }
        finally { _gate.Release(); }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            try
            {
                if (_refreshToken is not null)
                {
                    using var response = await _http.PostAsync(new Uri(Server, "connect/revoke"), new FormUrlEncodedContent(
                        new Dictionary<string, string> { ["client_id"] = ClientId, ["token"] = _refreshToken }), ct);
                    if (!response.IsSuccessStatusCode)
                        throw new CloudAuthenticationException("revocation_failed", "已退出本机账号，云端设备撤销未完成，请在 Intensive Listening 云服务 中检查登录设备。");
                }
            }
            finally { await ClearCoreAsync(); }
        }
        finally { _gate.Release(); }
    }

    public async Task InvalidateAsync(string rejectedAccessToken, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { if (_accessToken == rejectedAccessToken) await ClearCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        if (_loaded) return;
        _refreshToken = await _store.ReadAsync(ct);
        HasCredentials = !string.IsNullOrWhiteSpace(_refreshToken);
        if (!HasCredentials) _refreshToken = null;
        _loaded = true;
    }

    private async Task AcceptTokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var invalid = response.StatusCode == HttpStatusCode.Unauthorized;
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                try
                {
                    using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    invalid = error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error", out var field)
                        && field.ValueKind == JsonValueKind.String && field.GetString() is "invalid_grant" or "invalid_token";
                }
                catch (JsonException) { }
            }
            if (invalid) await ClearCoreAsync();
            throw new CloudAuthenticationException(invalid ? "login_required" : "token_request_failed",
                invalid ? "登录已失效，请重新授权 Intensive Listening 云服务。" : "Intensive Listening 云服务 登录请求失败，请稍后重试。");
        }
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(CancellationToken.None));
        var body = json.RootElement;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
            || !body.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString())
            || !body.TryGetProperty("refresh_token", out var refresh) || refresh.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(refresh.GetString())
            || !body.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number
            || !expires.TryGetInt32(out var seconds) || seconds is <= 0 or > 86400)
            throw new CloudAuthenticationException("invalid_token_response", "Intensive Listening 云服务 返回的登录信息无效。");
        // Once the server rotates a refresh token, finish saving it even if the user cancels.
        var rotated = refresh.GetString()!;
        _refreshToken = rotated;
        _accessToken = null;
        _loaded = true;
        try { await _store.WriteAsync(rotated, CancellationToken.None); }
        catch { await ClearCoreAsync(); throw; }
        _loaded = HasCredentials = true;
        _accessToken = access.GetString();
        DeviceId = body.TryGetProperty("device_id", out var device) && device.ValueKind == JsonValueKind.String ? device.GetString() : null;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private async Task ClearCoreAsync()
    {
        _accessToken = _refreshToken = null;
        DeviceId = null;
        _expiresAt = default;
        _loaded = true;
        HasCredentials = false;
        await _store.DeleteAsync(CancellationToken.None);
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
