using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IL.App.Views;
using IL.Core.Settings;

namespace IL.UiSnapshots;

internal static partial class Program
{
    private static async Task CheckMotionAsync()
    {
        var wizardType = AppAssembly.GetType("IL.App.Views.Dialogs.FirstRunWizard", true)!;
        var motion = wizardType.GetProperty("ReducedMotionOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        motion.SetValue(null, (Func<bool>)(() => false));
        var host = new TransitioningContentControl { PageTransition = new WorkspacePageTransition(), Content = new Border { Background = Brushes.White } };
        var owner = CreateWindow(new Grid { Children = { host } }); owner.Show(); await Task.Delay(350);
        var page = new Border { Background = Brushes.LightBlue, Child = new TextBlock { Text = "页面切换动画", FontSize = 32 } }; host.Content = page;
        await Task.Delay(90);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        await Task.Delay(20);
        var presenter = page.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        var opacity = presenter.Opacity; var y = (presenter.RenderTransform as TranslateTransform)?.Y;
        SaveMotionFrame(owner, "navigation-mid");
        if (opacity <= 0 || opacity >= 1 || y is null or <= 0 or >= 18) throw new InvalidOperationException($"Navigation did not animate: {opacity}, {y}");
        await Task.Delay(300);
        if (presenter.Opacity != 1 || presenter.RenderTransform is TranslateTransform { Y: not 0 }) throw new InvalidOperationException("Navigation did not settle.");
        var initial = new AppSettings { CloudApiKey = "", EulaAcceptedVersion = "", TelemetryEnabled = false, TelemetryPrompted = true };
        var welcome = (Window)wizardType.GetMethod("CreateWindowsWindow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [owner, initial, "用户协议测试", "隐私说明测试"])!;
        var completed = welcome.ShowDialog<AppSettings?>(owner);
        var wizard = ((Grid)welcome.Content!).Children.Single();
        await Task.Delay(700);
        var intro = welcome.GetVisualDescendants().OfType<Grid>().Single(c => c.Name == "OobeIntro");
        var scale = ((ScaleTransform)intro.RenderTransform!).ScaleX;
        SaveMotionFrame(welcome, "windows-oobe-intro");
        if (scale <= 1 || scale >= 1.25) throw new InvalidOperationException("Welcome intro did not advance.");
        await Task.Delay(4300); SaveMotionFrame(welcome, "windows-oobe-welcome");
        SetField(wizard, "_step", 1); Invoke(wizard, "Render"); await Task.Delay(100);
        var incoming = welcome.GetVisualDescendants().OfType<StackPanel>().Single(c => c.Name == "OobePage1");
        var stepOpacity = incoming.Opacity; var x = ((TranslateTransform)incoming.RenderTransform!).X;
        SaveMotionFrame(welcome, "windows-oobe-step-mid");
        if (stepOpacity <= 0 || stepOpacity >= 1 || x <= 0 || x >= 80) throw new InvalidOperationException($"OOBE did not animate: {stepOpacity}, {x}");
        await Task.Delay(350);
        var next = (Button)GetField(wizard, "_licenseNext")!;
        if (next.IsEnabled) throw new InvalidOperationException("Consent gate starts open.");
        var checks = incoming.GetLogicalDescendants().OfType<CheckBox>().ToArray();
        checks[0].IsChecked = true; if (next.IsEnabled) throw new InvalidOperationException("Single consent unlocked next.");
        checks[1].IsChecked = true; if (!next.IsEnabled) throw new InvalidOperationException("Both consents did not unlock next.");
        var names = new[] { "agreement", "basics", "appearance", "api", "done" };
        for (var step = 1; step <= 5; step++) { SetField(wizard, "_step", step); Invoke(wizard, "Render"); await CaptureAsync(welcome, "windows-oobe-" + names[step - 1]); }
        SetField(wizard, "_step", 1); Invoke(wizard, "Render"); await Task.Delay(350);
        if (!ReferenceEquals(incoming, welcome.GetVisualDescendants().OfType<StackPanel>().Single(c => c.Name == "OobePage1")) || !next.IsEnabled) throw new InvalidOperationException("Back navigation discarded cached consent state.");
        SetField(wizard, "_step", 5); Invoke(wizard, "Render"); await Task.Delay(350); Invoke(wizard, "Next");
        var settings = await completed;
        if (settings?.EulaAcceptedVersion != "2026-09-22" || initial.EulaAcceptedVersion != "") throw new InvalidOperationException("OOBE completion or initial settings isolation failed.");
        motion.SetValue(null, (Func<bool>)(() => true));
        var reduced = new Border { Background = Brushes.Pink }; host.Content = reduced; await Task.Delay(80);
        var reducedPresenter = reduced.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        if (reducedPresenter.Opacity != 1 || reducedPresenter.RenderTransform is TranslateTransform { Y: not 0 }) throw new InvalidOperationException("Reduced motion still animates.");
        motion.SetValue(null, null); owner.Close();
        await File.WriteAllTextAsync(Path.Combine(_output, "motion.json"), JsonSerializer.Serialize(new { NavigationMid = new { Opacity = opacity, Y = y }, WindowsWelcomeSize = new { welcome.Width, welcome.Height }, IntroScaleAt700ms = scale, OobeMid = new { Opacity = stepOpacity, X = x }, ConsentGate = "passed", CachedBackState = "passed", FinalOnlySettings = "passed", ReducedMotion = "passed", Boundary = "Headless Skia animation progression and state checks; native interaction acceptance belongs to the user" }, Json));
    }
    private static void SaveMotionFrame(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing motion frame");
        frame.Save(Path.Combine(_output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
