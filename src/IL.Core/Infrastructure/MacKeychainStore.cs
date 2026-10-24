using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace IL.Core.Infrastructure;

public static class MacKeychainStore
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

        private static nint Query(string serviceName, string accountName, bool read = false)
        {
            var service = CFStringCreateWithCString(0, serviceName, 0x08000100);
            var account = CFStringCreateWithCString(0, accountName, 0x08000100);
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

        public static string? Read(string serviceName, string accountName)
        {
            var query = Query(serviceName, accountName, true);
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

        public static void Write(string serviceName, string accountName, string token)
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            var data = CFDataCreate(0, bytes, bytes.Length);
            CryptographicOperations.ZeroMemory(bytes);
            var query = Query(serviceName, accountName);
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

        public static void Delete(string serviceName, string accountName)
        {
            var query = Query(serviceName, accountName);
            try { var status = SecItemDelete(query); if (status != -25300) Check(status); }
            finally { CFRelease(query); }
        }
        private static void Check(int status)
        {
            if (status != 0) throw new InvalidOperationException($"系统钥匙串操作失败（{status}）。");
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
