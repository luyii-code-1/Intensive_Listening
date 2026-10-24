using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IL.Core.Management;
namespace IL.Core.Updates;

public sealed class ManagedUpdateDownloader(string root, string publicKey, string certificatePin="", Uri? credentialOrigin = null, string? deviceToken = null)
{
    public void Validate(ManagedUpdate update)
    {
        if (update.Mode is not ("ota" or "cold") || !Guid.TryParse(update.Id, out _) || !ManagedVersion.TryParse(update.Version, out _) || update.Size is <= 0 or > 2L * 1024 * 1024 * 1024 || !Regex.IsMatch(update.Sha256, "^[a-fA-F0-9]{64}$") || !Uri.TryCreate(update.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0)
            throw new InvalidOperationException("更新描述无效。");
        using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!rsa.VerifyData(Encoding.UTF8.GetBytes(update.SignedContent), Convert.FromBase64String(update.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new InvalidOperationException("更新签名校验失败。");
    }
    public async Task<string> DownloadAsync(ManagedUpdate update, CancellationToken ct, Action<int>? progress = null)
    {
        Validate(update); Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.Parse(update.Id).ToString("N") + ".exe");
        if (File.Exists(path)) { try { await VerifyAsync(path, update, ct); return path; } catch (InvalidOperationException) { File.Delete(path); } }
        var target = new Uri(update.Url);
        var sameOrigin = credentialOrigin is not null && target.Scheme == credentialOrigin.Scheme && target.Authority == credentialOrigin.Authority;
        using var http = new HttpClient(MdnConfiguration.CreateHttpHandler(credentialOrigin is null || sameOrigin ? certificatePin : "")) { Timeout = Timeout.InfiniteTimeSpan };
        if (sameOrigin && deviceToken is not null) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deviceToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(20));
        var part = path + ".part";
        try
        {
            using var response = await http.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != update.Size) throw new InvalidOperationException("安装包大小不匹配。");
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long total = 0; int count;
                var lastProgress = -1;
                while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    total += count; if (total > update.Size) throw new InvalidOperationException("安装包超出声明大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    var percent = (int)(total * 100 / update.Size);
                    if (percent / 5 != lastProgress / 5 || lastProgress < 0) { lastProgress = percent; progress?.Invoke(percent); }
                }
            }
            await VerifyAsync(part, update, deadline.Token); File.Move(part, path, true); return path;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
    public async Task VerifyAsync(string path, ManagedUpdate update, CancellationToken ct)
    {
        Validate(update); if (new FileInfo(path).Length != update.Size) throw new InvalidOperationException("安装包大小校验失败。");
        await using var stream = File.OpenRead(path); var digest = await SHA256.HashDataAsync(stream, ct);
        if (!Convert.ToHexString(digest).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("安装包 SHA-256 校验失败。");
    }
}
