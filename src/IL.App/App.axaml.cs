using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace IL.App;

public partial class App : Application
{
    public override void Initialize() { AvaloniaXamlLoader.Load(this); Views.SpringMotion.InstallFeedback(); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Services.CrashMonitor.ReportPath is { } path ? new Views.Dialogs.CrashReportWindow(path) : new MainWindow();
            if (OperatingSystem.IsWindows() && desktop.MainWindow is MainWindow main && !Program.Arguments.Contains("--standalone"))
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Program.Instance?.SetActivationHandler(args => Dispatcher.UIThread.Post(async () => await main.ActivateFromLaunchAsync(args)));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
