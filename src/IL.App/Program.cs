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
        IL.Core.Infrastructure.AppLog.Install();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
