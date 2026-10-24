using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace IL.App;

public partial class App : Application
{
    public override void Initialize() { AvaloniaXamlLoader.Load(this); Views.MotionPreferences.Install(); Views.SpringMotion.InstallFeedback(); Views.PopupMotion.Install(); Views.SpringScroll.Install(); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = Services.CrashMonitor.ReportPath is { } path ? (Window)new Views.Dialogs.CrashReportWindow(path) : new MainWindow();
            var background = window is MainWindow && OperatingSystem.IsWindows() && Program.Arguments.Contains("--background-after-update");
            desktop.MainWindow = background ? null : window;
            if (background) Dispatcher.UIThread.Post(async () =>
            {
                desktop.MainWindow = window;
                await ((MainWindow)window).InitializeBackgroundAsync();
            });
            if (OperatingSystem.IsWindows() && window is MainWindow main && !Program.Arguments.Contains("--standalone"))
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Program.Instance?.SetActivationHandler(args => Dispatcher.UIThread.Post(async () => await main.ActivateFromLaunchAsync(args)));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
