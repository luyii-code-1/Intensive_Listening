using System.Diagnostics;
using System.Text.Json;
using IL.Core.Infrastructure;

namespace IL.App.Services;

internal static class CrashVerification
{
    public static async Task<int> RunAsync(string output)
    {
        var isolated = Path.Combine(Path.GetTempPath(), "il-crash-verification-" + Guid.NewGuid().ToString("N"));
        var checks = new List<object>(); Directory.CreateDirectory(isolated);
        try
        {
            foreach (var mode in new[] { "managed", "abrupt", "clean" })
            {
                var root = Path.Combine(isolated, mode); Directory.CreateDirectory(root);
                using var child = Process.Start(CrashMonitor.CreateStartInfo("--crash-probe", root, mode))!;
                if (mode == "abrupt")
                {
                    using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (!Directory.Exists(Path.Combine(root, "crash-reports", "sessions")) || !Directory.EnumerateFiles(Path.Combine(root, "crash-reports", "sessions"), "*.json").Any()) await Task.Delay(20, readyTimeout.Token);
                    // Wait for the monitor to launch before forcibly ending its parent.
                    await Task.Delay(750); child.Kill();
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await child.WaitForExitAsync(timeout.Token);
                var sessions = Path.Combine(root, "crash-reports", "sessions");
                while (Directory.Exists(sessions) && Directory.EnumerateFiles(sessions, "*.json").Any()) await Task.Delay(40, timeout.Token);
                var reports = new CrashReportStore(root).Reports();
                if (mode == "clean" ? reports.Count != 0 : reports.Count != 1) throw new InvalidOperationException($"{mode}: unexpected report count {reports.Count}");
                if (mode == "managed" && !reports[0].Details.Contains("Crash verification: managed exception")) throw new InvalidOperationException("Managed exception stack was lost.");
                if (reports.Any(r => r.UploadAllowed)) throw new InvalidOperationException("Verification unexpectedly enabled telemetry.");
                checks.Add(new { Mode = mode, ExitCode = child.ExitCode, Reports = reports.Count, Result = "passed" });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Checks = checks, Boundary = "Real child process crashes and monitor reports; crash window tested headlessly, native UI acceptance belongs to the user." }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if (Directory.Exists(isolated)) Directory.Delete(isolated, true); }
    }
}
