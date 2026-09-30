using System.Security.Cryptography;

namespace IL.Core.Ilp;

public static class FileHashing
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
    }
    public static async Task VerifySha256Async(string path, string expected, CancellationToken ct = default)
    {
        if (await Sha256Async(path, ct) != expected)
            throw new IlpException(IlpError.HashMismatch, $"文件 SHA-256 校验失败：{Path.GetFileName(path)}");
    }
}
