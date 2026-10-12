using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IL.Core.Management;

public sealed class ManagedLessonDownloader(string root, MdnConfiguration configuration, string? credential, HttpMessageHandler? handler = null)
{
    public void Validate(ManagedLesson item)
    {
        if (!Guid.TryParse(item.Id, out var id) || !Guid.TryParse(item.PackageUuid, out _) || item.PackageVersion < 1 || item.Size is <= 0 or > 2L * 1024 * 1024 * 1024 || !Regex.IsMatch(item.Sha256, "^[a-fA-F0-9]{64}$") ||
            !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri != new Uri(configuration.Server, "api/lessons/" + id.ToString("D")))
            throw new InvalidOperationException("组织精听包描述无效。");
        using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(configuration.UpdatePublicKey), out _);
        if (!rsa.VerifyData(Encoding.UTF8.GetBytes(item.SignedContent), Convert.FromBase64String(item.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new InvalidOperationException("组织精听包签名校验失败。");
    }
    public async Task<string> DownloadAsync(ManagedLesson item, CancellationToken ct)
    {
        Validate(item); Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.Parse(item.Id).ToString("D") + ".ilp");
        if (File.Exists(path)) { try { await VerifyAsync(path, item, ct); return path; } catch (InvalidOperationException) { File.Delete(path); } }
        using var http = new HttpClient(handler ?? MdnConfiguration.CreateHttpHandler(configuration.ServerCertificateSha256)) { Timeout = Timeout.InfiniteTimeSpan };
        if (credential is null) throw new InvalidOperationException("尚未取得设备凭证。");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(20));
        var part = path + ".part";
        try
        {
            using var response = await http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long size && size != item.Size) throw new InvalidOperationException("组织精听包大小不匹配。");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                { total += count; if (total > item.Size) throw new InvalidOperationException("组织精听包超过声明大小。"); await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token); }
            }
            await VerifyAsync(part, item, timeout.Token); File.Move(part, path, true); return path;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
    public async Task VerifyAsync(string path, ManagedLesson item, CancellationToken ct)
    {
        Validate(item);
        if (new FileInfo(path).Length != item.Size) throw new InvalidOperationException("组织精听包大小校验失败。");
        await using var input = File.OpenRead(path);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("组织精听包 SHA-256 校验失败。");
    }
}
