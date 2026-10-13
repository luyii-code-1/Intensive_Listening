using Microsoft.Win32;
namespace IL.Core.Management;

public static class DeviceIdentity
{
    private static readonly object Gate = new();
    public static string GetOrCreate(string root)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "device-id.txt");
            var saved = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            string? registered = null;
            if (OperatingSystem.IsWindows())
            {
                using var machine = Registry.LocalMachine.OpenSubKey(@"Software\Intensive Listening");
                using var user = Registry.CurrentUser.OpenSubKey(@"Software\Intensive Listening");
                registered = machine?.GetValue("InstallUuid") as string ?? user?.GetValue("InstallUuid") as string;
            }
            // An installed machine identity is authoritative when repairing a mismatched data copy.
            var id = Guid.TryParse(registered, out var previous) ? previous : Guid.TryParse(saved, out var existing) ? existing : Guid.NewGuid();
            var value = id.ToString("D");
            if (saved != value) { File.WriteAllText(path + ".tmp", value); File.Move(path + ".tmp", path, true); }
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Intensive Listening");
                key.SetValue("InstallUuid", value);
            }
            return value;
        }
    }
    public static string InstallationCycle(string root)
    {
        var path = Path.Combine(root, "installation-cycle.txt");
        if (!File.Exists(path))
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "installation-id.txt");
            Directory.CreateDirectory(root);
            File.WriteAllText(path, File.Exists(legacy) ? File.ReadAllText(legacy).Trim() : Guid.NewGuid().ToString("D"));
        }
        return File.ReadAllText(path).Trim();
    }
}
