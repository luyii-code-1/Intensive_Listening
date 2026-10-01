using System.Diagnostics;
using System.Text.Json;

namespace IL.App.Services;

public static class InstanceVerification
{
    public static int Run(string output)
    {
        var name = "il2-instance-verification-" + Guid.NewGuid().ToString("N");
        string[]? received = null; var checks = new List<string>(); Exception? failure = null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        try
        {
            using (var owner = SingleInstanceSession.Acquire(name) ?? throw new InvalidOperationException("Cannot acquire primary instance."))
            {
                using var arrived = new ManualResetEventSlim();
                owner.SetActivationHandler(args => { received = args; arrived.Set(); });
                using var child = Process.Start(CrashMonitor.CreateStartInfo("--instance-probe", name, "C:\\听力课程\\期末 考试.ilp")) ?? throw new InvalidOperationException("Cannot start activation probe.");
                if (!child.WaitForExit(15000)) { child.Kill(true); throw new TimeoutException("Activation probe timed out."); }
                if (child.ExitCode != 0 || !arrived.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Secondary process did not forward its activation request.");
                if (received is not ["C:\\听力课程\\期末 考试.ilp"]) throw new InvalidOperationException("Unicode activation path changed.");
                checks.Add("secondary process uses existing instance"); checks.Add("Unicode file-open forwarded");
            }
            using var reopened = SingleInstanceSession.Acquire(name) ?? throw new InvalidOperationException("Exited instance retained ownership.");
            checks.Add("ownership released for restart");
        }
        catch (Exception error) { failure = error; }
        File.WriteAllText(output, JsonSerializer.Serialize(new { success = failure == null, build = AppBuildInfo.Commit, checks, error = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        return failure == null ? 0 : 1;
    }
    public static int Probe(string name, string path)
    {
        using var duplicate = SingleInstanceSession.Acquire(name);
        if (duplicate != null) return 1;
        return SingleInstanceSession.NotifyAsync(name, [path]).GetAwaiter().GetResult() ? 0 : 1;
    }
}
