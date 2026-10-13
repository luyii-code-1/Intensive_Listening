using System.Net.Http.Json;
using System.Security.Cryptography;

namespace IL.Core.Management;

public sealed record MdnServerInfo(string OrganizationName, string UpdatePublicKey, string CertificateSha256);

public static class MdnEnrollment
{
    public static Uri NormalizeAddress(string address, bool disableSsl = false)
    {
        address = address.Trim();
        if (!address.Contains("://", StringComparison.Ordinal)) address = (disableSsl ? "http://" : "https://") + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != (disableSsl ? "http" : "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new FormatException("请输入与 SSL 选项匹配的服务器 IP:端口或 HTTP/HTTPS 地址。");
        return uri;
    }

    // Discovery carries no enrollment credential. The UI must confirm the observed
    // fingerprint before registration uses a separate, strictly pinned connection.
    public static async Task<MdnServerInfo> DiscoverAsync(Uri server, CancellationToken ct = default)
    {
        string? observed = null;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            observed = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
            return true;
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 32 * 1024 };
        var info = await http.GetFromJsonAsync<MdnServerInfo>(new Uri(server, "api/info"), ct)
            ?? throw new FormatException("服务器没有返回组织信息。");
        if (string.IsNullOrWhiteSpace(info.OrganizationName) || info.OrganizationName.Length > 120
            || server.Scheme == "https" && (observed is null || !string.Equals(info.CertificateSha256, observed, StringComparison.OrdinalIgnoreCase)))
            throw new FormatException("服务器身份信息与证书不一致。");
        using var signing = RSA.Create(); signing.ImportSubjectPublicKeyInfo(Convert.FromBase64String(info.UpdatePublicKey), out _);
        return info with { CertificateSha256 = server.Scheme == "https" ? observed! : "" };
    }
}
