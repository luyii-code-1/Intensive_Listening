using System.Security.Cryptography;
using System.Text;

namespace IL.Core.Cloud;

public sealed class CloudAuthorizationRequest
{
    private readonly string _state = Base64Url(RandomNumberGenerator.GetBytes(32));
    private readonly string _verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
    public Uri RedirectUri { get; }
    internal string Verifier => _verifier;

    public CloudAuthorizationRequest(Uri redirectUri)
    {
        if (redirectUri.Scheme != "http" || redirectUri.Host != "127.0.0.1" || redirectUri.Port < 1024 || redirectUri.IsDefaultPort
            || redirectUri.AbsolutePath != "/callback" || redirectUri.Query.Length != 0
            || redirectUri.Fragment.Length != 0 || redirectUri.UserInfo.Length != 0)
            throw new ArgumentException("登录回调必须使用 127.0.0.1 的临时端口和 /callback。", nameof(redirectUri));
        RedirectUri = redirectUri;
    }

    public Uri BrowserUri(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Trim().Length > 80 || deviceName.Any(char.IsControl))
            throw new ArgumentException("设备名称应为 1–80 个字符。", nameof(deviceName));
        var values = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = CloudAuthClient.ClientId,
            ["redirect_uri"] = RedirectUri.AbsoluteUri,
            ["code_challenge"] = Challenge(_verifier), ["code_challenge_method"] = "S256",
            ["state"] = _state, ["scope"] = CloudAuthClient.Scope,
            ["device_name"] = deviceName.Trim()
        };
        return new Uri(CloudAuthClient.Server, "connect/authorize?" + string.Join("&", values.Select(
            pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    }

    public string ReadCode(Uri callback)
    {
        if (callback.GetLeftPart(UriPartial.Path) != RedirectUri.AbsoluteUri || callback.Fragment.Length != 0)
            throw new CloudAuthenticationException("invalid_callback", "登录回调地址不匹配。");
        var values = ParseQuery(callback.Query);
        if (!values.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(_state)))
            throw new CloudAuthenticationException("invalid_state", "登录回调校验失败，请重新登录。");
        if (values.TryGetValue("error", out var error))
            throw new CloudAuthenticationException(error == "access_denied" ? error : "authorization_failed",
                error == "access_denied" ? "已取消 IL Cloud 授权。" : "IL Cloud 授权失败，请重试。");
        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            throw new CloudAuthenticationException("invalid_code", "登录回调缺少有效授权码。");
        return code;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = field.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (!result.TryAdd(key, value)) throw new CloudAuthenticationException("invalid_callback", "登录回调包含重复字段。");
        }
        return result;
    }

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class CloudAuthenticationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
