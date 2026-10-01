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
using IL.App.Views.Dialogs;
using IL.Core.Infrastructure;
using IL.Core.Settings;

namespace IL.UiSnapshots;

internal static partial class Program
{
    private static async Task CheckMotionAsync()
    {
        var wizardType = AppAssembly.GetType("IL.App.Views.Dialogs.FirstRunWizard", true)!;
        var motion = wizardType.GetProperty("ReducedMotionOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        motion.SetValue(null, (Func<bool>)(() => false));
        var host = new WorkspaceContentHost { Content = new Border { Background = Brushes.White } };
        var owner = CreateWindow(new Grid { Children = { host } }); owner.Show(); await Task.Delay(350);
        var page = new Border { Background = Brushes.LightBlue, Child = new TextBlock { Text = "页面切换动画", FontSize = 32 } }; host.Content = page;
        if (host.Children.Last().Opacity != 0) throw new InvalidOperationException("Incoming page was visible before its entrance started.");
        await Task.Delay(90);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        await Task.Delay(20);
        var presenter = page.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        var opacity = presenter.Opacity; var y = (presenter.RenderTransform as TransformGroup)?.Children.OfType<TranslateTransform>().First().Y;
        SaveMotionFrame(owner, "navigation-mid");
        if (opacity <= 0 || opacity >= 1 || y is null or <= 0 or >= 12) throw new InvalidOperationException($"Navigation did not animate: {opacity}, {y}");
        await Task.Delay(650);
        if (presenter.Opacity != 1 || ((TransformGroup)presenter.RenderTransform!).Children.OfType<TranslateTransform>().First().Y != 0) throw new InvalidOperationException("Navigation did not settle.");
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
        var stepOpacity = incoming.Opacity; var x = ((TransformGroup)incoming.RenderTransform!).Children.OfType<TranslateTransform>().First().X;
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
        if (reducedPresenter.Opacity is <= 0 or >= 1 || ((TransformGroup)reducedPresenter.RenderTransform!).Children.OfType<TranslateTransform>().First().Y != 0) throw new InvalidOperationException("Reduced motion lost its gentle fade or retained spatial movement.");
        await Task.Delay(450);
        if (reducedPresenter.Opacity != 1) throw new InvalidOperationException("Reduced motion did not settle.");
        motion.SetValue(null, (Func<bool>)(() => false));
        for (var i = 0; i < 12; i++) { host.Content = i % 2 == 0 ? page : reduced; await Task.Delay(24); }
        await Task.Delay(700);
        if (host.Children.Count != 1 || host.Children[0].Opacity != 1 || !ReferenceEquals(((Avalonia.Controls.Presenters.ContentPresenter)host.Children[0]).Content, reduced)) throw new InvalidOperationException("Rapid navigation retained an old presenter or an incomplete spring.");
        var disclosure = new Expander { Header = "展开测试", Content = new Border { Height = 120, Child = new TextBlock { Text = "内容" } } };
        host.Content = disclosure; await Task.Delay(700);
        if (disclosure.ContentTransition is not SpringDisclosureTransition) throw new InvalidOperationException("Disclosure did not use the spring transition.");
        disclosure.IsExpanded = true; await Task.Delay(80);
        var contentPresenter = disclosure.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(c => c.TemplatedParent == disclosure);
        var disclosureMid = contentPresenter.Height;
        if (disclosureMid is <= 0 or >= 120) throw new InvalidOperationException($"Disclosure skipped its middle frames: {disclosureMid}");
        disclosure.IsExpanded = false; await Task.Delay(30); disclosure.IsExpanded = true; await Task.Delay(750);
        if (!contentPresenter.IsVisible || !double.IsNaN(contentPresenter.Height)) throw new InvalidOperationException("Disclosure reversal did not restore automatic sizing.");
        disclosure.IsExpanded = false; await Task.Delay(750);
        if (contentPresenter.IsVisible) throw new InvalidOperationException("Disclosure did not close.");
        var toggle = new Avalonia.Controls.Primitives.ToggleButton { Content = "选中反馈" };
        host.Content = toggle; await Task.Delay(700); toggle.IsChecked = true; await Task.Delay(65);
        var feedbackScale = ((TransformGroup)toggle.RenderTransform!).Children.OfType<ScaleTransform>().First().ScaleX;
        if (feedbackScale is >= 1 or <= .97) throw new InvalidOperationException("Selection feedback did not progress through a spring.");
        var dialog = AppDialogs.Create(owner, "弹窗动画", new TextBlock { Text = "消息" }); dialog.CloseButtonText = "关闭";
        var showing = dialog.ShowAsync(owner); await Task.Delay(80);
        var dialogSurface = dialog.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "BackgroundElement");
        if (dialogSurface.Opacity is <= 0 or >= 1) throw new InvalidOperationException("Dialog spring entrance skipped middle frames.");
        var dialogMid = dialogSurface.Opacity; await Task.Delay(600); dialog.Hide(); await showing;
        var fixtureRoot = Path.Combine(_output, "crash-window-fixture");
        var crashStore = new CrashReportStore(fixtureRoot);
        var crash = CrashReport.FromException(new InvalidOperationException("审阅列表行创建失败"), "审阅", true, false); crashStore.Save(crash);
        var crashWindow = new CrashReportWindow(crashStore.PathFor(crash.Id), submit: false); crashWindow.Show(owner);
        await CaptureAsync(crashWindow, "crash-report-window"); crashWindow.Close();
        if (!CrashReportStore.Load(crashStore.PathFor(crash.Id))!.Displayed) throw new InvalidOperationException("Crash window did not mark its report as displayed.");
        motion.SetValue(null, null); owner.Close();
        await File.WriteAllTextAsync(Path.Combine(_output, "motion.json"), JsonSerializer.Serialize(new { NavigationMid = new { Opacity = opacity, Y = y }, WindowsWelcomeSize = new { welcome.Width, welcome.Height }, IntroScaleAt700ms = scale, OobeMid = new { Opacity = stepOpacity, X = x }, ConsentGate = "passed", CachedBackState = "passed", FinalOnlySettings = "passed", DisclosureMidHeight = disclosureMid, DisclosureReversal = "passed", SelectionScale = feedbackScale, DialogMidOpacity = dialogMid, CrashWindow = "offscreen rendered", ReducedMotion = "gentle spring fade passed", RapidNavigation = "12 reversals passed", PreAttachmentOpacity = "zero passed", Boundary = "Headless Skia animation progression and state checks; native interaction acceptance belongs to the user" }, Json));
    }
    private static void SaveMotionFrame(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing motion frame");
        frame.Save(Path.Combine(_output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
