using Avalonia;
using IL.App.Services;

namespace IL.App;

internal static class Program
{
    public static string[] Arguments {get;private set;} = [];
    [STAThread]
    public static int Main(string[] args)
    {
        Arguments=args;
        var verification=Array.IndexOf(args,"--verify-runtime");
        if(verification>=0)return RuntimeVerification.RunAsync(verification+1<args.Length?args[verification+1]:"verification.json").GetAwaiter().GetResult();
        var crashVerification = Array.IndexOf(args, "--verify-crash");
        if (crashVerification >= 0) return CrashVerification.RunAsync(args[crashVerification + 1]).GetAwaiter().GetResult();
        var probe = Array.IndexOf(args, "--crash-probe");
        if (probe >= 0)
        {
            CrashMonitor.Start(args[probe + 1], showReportWindows: false);
            if (args[probe + 2] == "managed") throw new InvalidOperationException("Crash verification: managed exception");
            if (args[probe + 2] == "clean") { CrashMonitor.CleanExit(); return 0; }
            Thread.Sleep(Timeout.Infinite); return 0;
        }
        var headlessWatch = Array.IndexOf(args, "--watch-crash-headless");
        if (headlessWatch >= 0) return CrashMonitor.WatchAsync(args[headlessWatch + 1], false).GetAwaiter().GetResult();
        var watch = Array.IndexOf(args, "--watch-crash");
        if (watch >= 0) return CrashMonitor.WatchAsync(args[watch + 1]).GetAwaiter().GetResult();
        var report = Array.IndexOf(args, "--crash-report");
        if (report >= 0) { CrashMonitor.SetReportPath(args[report + 1]); return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        CrashMonitor.Start();
        try
        {
            var result = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            CrashMonitor.CleanExit(); return result;
        }
        catch (Exception error) { CrashMonitor.Fatal(error); return 1; }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
        .With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = Views.Dialogs.WorkspaceUi.BodyFont.ToString() }).LogToTrace();
}
