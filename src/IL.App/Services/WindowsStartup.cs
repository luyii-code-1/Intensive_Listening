using Microsoft.Win32;

namespace IL.App.Services;

internal static class WindowsStartup
{
    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        const string name = "Intensive Listening";
        if (!enabled) { run.DeleteValue(name, throwOnMissingValue: false); return; }
        var start = CrashMonitor.CreateStartInfo();
        var command = "\"" + start.FileName + "\"";
        foreach (var arg in start.ArgumentList) command += " \"" + arg + "\"";
        if (!Equals(run.GetValue(name), command)) run.SetValue(name, command);
    }
}
