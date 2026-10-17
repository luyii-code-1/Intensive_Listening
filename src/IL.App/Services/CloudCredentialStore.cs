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
        if (OperatingSystem.IsMacOS()) return MacKeychain.Read();
        RequireWindows();
        if (!CredReadW(Account, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "无法读取 IL Cloud 系统凭据。");
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
        if (OperatingSystem.IsMacOS()) { MacKeychain.Write(token); return; }
        RequireWindows();
        var bytes = Encoding.UTF8.GetBytes(token);
        if (bytes.Length > 2560) { CryptographicOperations.ZeroMemory(bytes); throw new InvalidOperationException("IL Cloud 刷新令牌超过系统凭据容量。"); }
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Account, UserName = "il-desktop", Persist = 2,
                Comment = Service, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob };
            if (!CredWriteW(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 IL Cloud 系统凭据。");
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
        if (OperatingSystem.IsMacOS()) { MacKeychain.Delete(); return; }
        RequireWindows();
        if (!CredDeleteW(Account, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法删除 IL Cloud 系统凭据。");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("IL Cloud 系统凭据支持 Windows 和 macOS。");
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

    private static class MacKeychain
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly nint SecurityHandle = NativeLibrary.Load(Security);
        private static readonly nint FoundationHandle = NativeLibrary.Load(CoreFoundation);
        private static nint Symbol(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
        private static nint Sec(string name) => Symbol(SecurityHandle, name);
        private static nint Dictionary(nint[] keys, nint[] values) => CFDictionaryCreate(0, keys, values, keys.Length,
            NativeLibrary.GetExport(FoundationHandle, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(FoundationHandle, "kCFTypeDictionaryValueCallBacks"));

        private static nint Query(bool read = false)
        {
            var service = CFStringCreateWithCString(0, Service, 0x08000100);
            var account = CFStringCreateWithCString(0, Account, 0x08000100);
            try
            {
                nint[] keys = [Sec("kSecClass"), Sec("kSecAttrService"), Sec("kSecAttrAccount")];
                nint[] values = [Sec("kSecClassGenericPassword"), service, account];
                if (read)
                {
                    keys = [..keys, Sec("kSecReturnData"), Sec("kSecMatchLimit")];
                    values = [..values, Symbol(FoundationHandle, "kCFBooleanTrue"), Sec("kSecMatchLimitOne")];
                }
                return Dictionary(keys, values);
            }
            finally { CFRelease(service); CFRelease(account); }
        }

        public static string? Read()
        {
            var query = Query(true);
            nint data = 0;
            try
            {
                var status = SecItemCopyMatching(query, out data);
                if (status == -25300) return null;
                Check(status);
                var bytes = new byte[checked((int)CFDataGetLength(data))];
                try { Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { if (data != 0) CFRelease(data); CFRelease(query); }
        }

        public static void Write(string token)
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            var data = CFDataCreate(0, bytes, bytes.Length);
            CryptographicOperations.ZeroMemory(bytes);
            var query = Query();
            var update = Dictionary([Sec("kSecValueData")], [data]);
            try
            {
                var status = SecItemUpdate(query, update);
                if (status == -25300)
                {
                    var complete = CFDictionaryCreateMutableCopy(0, 0, query);
                    try { CFDictionarySetValue(complete, Sec("kSecValueData"), data); status = SecItemAdd(complete, 0); }
                    finally { CFRelease(complete); }
                }
                Check(status);
            }
            finally { CFRelease(update); CFRelease(query); CFRelease(data); }
        }

        public static void Delete()
        {
            var query = Query();
            try { var status = SecItemDelete(query); if (status != -25300) Check(status); }
            finally { CFRelease(query); }
        }
        private static void Check(int status)
        {
            if (status != 0) throw new InvalidOperationException($"IL Cloud 钥匙串操作失败（{status}）。");
        }
        [DllImport(Security)] private static extern int SecItemCopyMatching(nint query, out nint result);
        [DllImport(Security)] private static extern int SecItemUpdate(nint query, nint values);
        [DllImport(Security)] private static extern int SecItemAdd(nint attributes, nint result);
        [DllImport(Security)] private static extern int SecItemDelete(nint query);
        [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
        [DllImport(CoreFoundation)] private static extern nint CFDictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallbacks, nint valueCallbacks);
        [DllImport(CoreFoundation)] private static extern nint CFDictionaryCreateMutableCopy(nint allocator, nint capacity, nint dictionary);
        [DllImport(CoreFoundation)] private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
        [DllImport(CoreFoundation)] private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
        [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(nint data);
        [DllImport(CoreFoundation)] private static extern nint CFDataGetBytePtr(nint data);
        [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    }
}
