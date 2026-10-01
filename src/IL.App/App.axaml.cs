using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace IL.App;

public partial class App : Application
{
    public override void Initialize() { AvaloniaXamlLoader.Load(this); Views.SpringMotion.InstallFeedback(); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = Services.CrashMonitor.ReportPath is { } path ? new Views.Dialogs.CrashReportWindow(path) : new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
