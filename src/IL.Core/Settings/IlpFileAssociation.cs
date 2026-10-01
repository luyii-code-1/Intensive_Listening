using System.Diagnostics;
using Microsoft.Win32;
namespace IL.Core.Settings;
public sealed class IlpFileAssociation
{
    public Task ApplyAsync(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        const string classes = @"Software\Classes\";
        if (enabled)
        {
            var executable = Environment.GetEnvironmentVariable("ILP_PORTABLE_LAUNCHER") ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用路径");
            foreach (var (key, value) in new[] { (".ilp", "IntensiveListening.ilp"), ("IntensiveListening.ilp", "Intensive Listening 精听包"), (@"IntensiveListening.ilp\DefaultIcon", $"\"{executable}\",0"), (@"IntensiveListening.ilp\shell\open\command", $"\"{executable}\" \"%1\"") }) { using var registry = Registry.CurrentUser.CreateSubKey(classes + key); registry.SetValue("", value); }
        }
        else { Registry.CurrentUser.DeleteSubKeyTree(classes + "IntensiveListening.ilp", false); Registry.CurrentUser.DeleteSubKeyTree(classes + ".ilp", false); }
        Process.Start(new ProcessStartInfo("ie4uinit.exe", "-show") { CreateNoWindow = true, UseShellExecute = false });
        return Task.CompletedTask;
    }
}
