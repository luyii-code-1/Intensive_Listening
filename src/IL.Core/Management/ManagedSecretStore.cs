using System.Security.Cryptography;
using System.Text;
using IL.Core.Infrastructure;

namespace IL.Core.Management;

/// <summary>Windows keeps the existing DPAPI format; macOS seals files with a Keychain key.</summary>
public static class ManagedSecretStore
{
    private static readonly object Gate = new();
    private static readonly byte[] Magic = "ILMDN01"u8.ToArray();
    private const string Service = "Intensive Listening MDN", Account = "cache-key-v1";

    public static void Save(string path, string value)
    {
        if (OperatingSystem.IsWindows()) { WindowsSecretStore.Save(path, value); return; }
        RequireMac();
        var key = Key(create: true);
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            var sealedValue = Seal(plaintext, key, Path.GetFileName(path));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path + ".tmp", sealedValue);
            File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static string Load(string path)
    {
        if (OperatingSystem.IsWindows()) return WindowsSecretStore.Load(path);
        RequireMac();
        var key = Key(create: false);
        byte[]? plaintext = null;
        try { plaintext = Open(File.ReadAllBytes(path), key, Path.GetFileName(path)); return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(key); if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal static byte[] Seal(byte[] plaintext, byte[] key, string purpose)
    {
        var output = new byte[Magic.Length + 12 + 16 + plaintext.Length];
        Magic.CopyTo(output, 0);
        var nonce = output.AsSpan(Magic.Length, 12); RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, output.AsSpan(Magic.Length + 28), output.AsSpan(Magic.Length + 12, 16), Encoding.UTF8.GetBytes(purpose));
        return output;
    }

    internal static byte[] Open(byte[] envelope, byte[] key, string purpose)
    {
        if (envelope.Length < Magic.Length + 28 || !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("组织缓存格式无效。");
        var plaintext = new byte[envelope.Length - Magic.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(envelope.AsSpan(Magic.Length, 12), envelope.AsSpan(Magic.Length + 28), envelope.AsSpan(Magic.Length + 12, 16), plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }

    private static byte[] Key(bool create)
    {
        lock (Gate)
        {
            var stored = MacKeychainStore.Read(Service, Account);
            if (stored is null)
            {
                if (!create) throw new CryptographicException("组织缓存的钥匙串密钥不存在。");
                var fresh = RandomNumberGenerator.GetBytes(32);
                try { MacKeychainStore.Write(Service, Account, Convert.ToBase64String(fresh)); }
                finally { CryptographicOperations.ZeroMemory(fresh); }
                stored = MacKeychainStore.Read(Service, Account) ?? throw new CryptographicException("组织缓存密钥保存失败。");
            }
            var key = Convert.FromBase64String(stored);
            if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new CryptographicException("组织缓存密钥无效。"); }
            return key;
        }
    }
    private static void RequireMac()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("组织凭据存储支持 Windows 和 macOS。");
    }
}
