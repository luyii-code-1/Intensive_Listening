using System.Security.Cryptography;
using System.Text;
using IL.Core.Management;

if (!OperatingSystem.IsWindows() || args.Length > 1) return 1;
try
{
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args.Length == 1 ? args[0] :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intensive Listening", "data")));
    var name = "Local\\IntensiveListening.2." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];
    using var mutex = new Mutex(false, name);
    bool owned;
    try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
    if (!owned) return ManagedStateRestore.ApplicationRunning;
    try { return ManagedStateRestore.Apply(root); }
    finally { mutex.ReleaseMutex(); }
}
catch (Exception error) when (error is not OutOfMemoryException) { return 1; }
