using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using IL.Core.Infrastructure;
using IL.Core.Settings;

namespace IL.App.Services;

public static class CrashMonitor
{
    private static readonly object Gate = new();
    private static CrashSession? _session;
    private static string? _sessionPath, _fatalReport;
    private static bool _watching, _showReportWindows = true;
    public static string? ReportPath { get; private set; }
    public static event Action<CrashReport>? BackgroundFault;
    public static void Start(string? dataDirectory = null, bool showReportWindows = true)
    {
        var root = dataDirectory ?? AppDirectories.DataDirectory();
        _showReportWindows = showReportWindows; var id = Guid.NewGuid().ToString("N");
        _sessionPath = Path.Combine(root, "crash-reports", "sessions", id + ".json");
        var settings = new AppSettingsStore(Path.Combine(root, "settings.json")).LoadAsync().GetAwaiter().GetResult();
        _session = new(id, Environment.ProcessId, root, DateTimeOffset.UtcNow, UploadAllowed: settings.TelemetryEnabled && !Program.Arguments.Contains("--standalone"));
        SaveSession();
        try { _watching = Launch(showReportWindows ? "--watch-crash" : "--watch-crash-headless", _sessionPath); } catch (Exception e) { AppLog.Warning("崩溃监视器启动失败", e); }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var report = CrashReport.FromException(e.Exception, "后台任务", false, _session?.UploadAllowed == true);
            new CrashReportStore(root).Save(report); AppLog.Error("后台任务意外退出", e.Exception); e.SetObserved(); BackgroundFault?.Invoke(report);
        };
    }
    public static void SetConsent(bool allowed) { lock (Gate) { if (_session != null) { _session = _session with { UploadAllowed = allowed }; SaveSession(); } } }
    public static void CleanExit() { lock (Gate) { if (_session != null) { _session = _session with { CleanExit = true }; SaveSession(); } } }
    public static void Fatal(Exception error)
    {
        lock (Gate)
        {
            if (_session == null || _fatalReport != null) return;
            try
            {
                var report = CrashReport.FromException(error, "主程序", true, _session.UploadAllowed);
                var store = new CrashReportStore(_session.Root); store.Save(report); _fatalReport = store.PathFor(report.Id);
                _session = _session with { ReportId = report.Id }; SaveSession();
                if (!_watching && _showReportWindows) Launch("--crash-report", _fatalReport);
            }
            catch (Exception e) { Console.Error.WriteLine("Crash report could not be saved: " + e.Message); }
        }
    }
    private static void SaveSession()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_sessionPath!)!);
        var temp = _sessionPath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(_session)); File.Move(temp, _sessionPath!, true);
    }
    public static async Task<int> WatchAsync(string path, bool showWindow = true)
    {
        var session = JsonSerializer.Deserialize<CrashSession>(await File.ReadAllTextAsync(path))!;
        int? exitCode = null;
        try { using var parent = Process.GetProcessById(session.ProcessId); await parent.WaitForExitAsync(); try { exitCode = parent.ExitCode; } catch (InvalidOperationException) { } }
        catch (ArgumentException) { }
        session = JsonSerializer.Deserialize<CrashSession>(await File.ReadAllTextAsync(path))!;
        if (session.CleanExit) { File.Delete(path); return 0; }
        var store = new CrashReportStore(session.Root);
        var report = session.ReportId != null ? CrashReportStore.Load(store.PathFor(session.ReportId)) : null;
        report ??= new CrashReport { Id = session.Id, ProcessId = session.ProcessId, ExitCode = exitCode, UploadAllowed = session.UploadAllowed,
            Details = "监视器检测到进程未完成正常关闭。可能为原生组件异常、强制终止或运行时故障；可同时查看系统诊断报告。" };
        store.Save(report); File.Delete(path);
        if (showWindow) Launch("--crash-report", store.PathFor(report.Id));
        return 0;
    }
    public static void SetReportPath(string path) => ReportPath = path;
    public static bool Launch(params string[] args) => Process.Start(CreateStartInfo(args)) is not null;
    internal static ProcessStartInfo CreateStartInfo(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
}
