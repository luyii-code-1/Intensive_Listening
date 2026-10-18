using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using IL.Core.Cloud;

namespace IL.App.Services;

public sealed class CloudCredentialStore : ICloudTokenStore
{
    private const string Service = "Intensive Listening IL Cloud";
    private const string Account = "cloud.il.luyii.cn/il-desktop";
    public Task<string?> ReadAsync(CancellationToken ct = default) => Task.Run(Read, ct);
    public Task WriteAsync(string refreshToken, CancellationToken ct = default) => Task.Run(() => Write(refreshToken), ct);
    public Task DeleteAsync(CancellationToken ct = default) => Task.Run(Delete, ct);

    private static string? Read()
    {
        if (OperatingSystem.IsMacOS()) return IL.Core.Infrastructure.MacKeychainStore.Read(Service, Account);
        RequireWindows();
        if (!CredReadW(Account, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "无法读取 Intensive Listening 云服务 系统凭据。");
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[checked((int)value.CredentialBlobSize)];
            try { Marshal.Copy(value.CredentialBlob, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static void Write(string token)
    {
        if (OperatingSystem.IsMacOS()) { IL.Core.Infrastructure.MacKeychainStore.Write(Service, Account, token); return; }
        RequireWindows();
        var bytes = Encoding.UTF8.GetBytes(token);
        if (bytes.Length > 2560) { CryptographicOperations.ZeroMemory(bytes); throw new InvalidOperationException("Intensive Listening 云服务 刷新令牌超过系统凭据容量。"); }
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Account, UserName = "il-desktop", Persist = 2,
                Comment = Service, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob };
            if (!CredWriteW(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Intensive Listening 云服务 系统凭据。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    private static void Delete()
    {
        if (OperatingSystem.IsMacOS()) { IL.Core.Infrastructure.MacKeychainStore.Delete(Service, Account); return; }
        RequireWindows();
        if (!CredDeleteW(Account, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法删除 Intensive Listening 云服务 系统凭据。");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Intensive Listening 云服务 系统凭据支持 Windows 和 macOS。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);

}
