using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using IL.App.Services;
using IL.App.ViewModels;
using IL.App.Views;
using IL.Core.Ilp;
using IL.Core.Infrastructure;
using IL.Core.Models;
using IL.Core.Projects;
using IL.Core.Settings;
using IL.Core.Student;
using IL.Core.Transcription;

namespace IL.UiSnapshots;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Assembly AppAssembly = typeof(IL.App.App).Assembly;
    private static readonly Type WorkspaceUi = AppAssembly.GetType("IL.App.Views.Dialogs.WorkspaceUi", throwOnError: true)!;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly List<object> Captures = [];
    private static string _output = "";
    private static double _width = 1440, _height = 900;
    private static bool _dark, _paneExpanded;

    [STAThread]
    public static int Main(string[] args)
    {
        var isolated = Path.Combine(Path.GetTempPath(), "il-ui-snapshots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = ParseOptions(args);
            _output = Path.GetFullPath(options.Output);
            _width = options.Width; _height = options.Height; _dark = options.Dark;
            Directory.CreateDirectory(_output); Directory.CreateDirectory(isolated);
            AppLog.DirectoryOverride = Path.Combine(isolated, "logs");
            AppBuilder.Configure<IL.App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            AvaloniaSynchronizationContext.InstallIfNeeded();
            Application.Current!.RequestedThemeVariant = _dark ? ThemeVariant.Dark : ThemeVariant.Light;
            var task = RenderAsync(isolated);
            var timeout = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (timeout.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException("Offscreen snapshots did not complete within two minutes.");
                Thread.Sleep(5);
            }
            task.GetAwaiter().GetResult();
            Console.WriteLine($"Rendered {Captures.Count} snapshots to {_output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            AppLog.DirectoryOverride = null;
            if (Directory.Exists(isolated)) Directory.Delete(isolated, true);
        }
    }

    private sealed record Options(string Output, double Width, double Height, bool Dark);
    private static Options ParseOptions(string[] args)
    {
        var output = "artifacts/ui-reference/csharp"; var width = 1440d; var height = 900d; var dark = false;
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--output": output = args[++i]; break;
                case "--width": width = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--height": height = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--dark": dark = true; break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        if (width < 720 || height < 560) throw new ArgumentException("Use a supported desktop size of at least 720 × 560.");
        return new(output, width, height, dark);
    }

    private static async Task RenderAsync(string isolated)
    {
        var fonts = DiagnoseFonts();
        await File.WriteAllTextAsync(Path.Combine(_output, "fonts.json"), JsonSerializer.Serialize(fonts, Json));
        foreach (var font in fonts.Fonts) if (!font.Resolved || !font.RequiredGlyphsPresent) throw new InvalidOperationException($"Embedded font did not resolve: {font.RequestedFamily} -> {font.ResolvedFamily}. See fonts.json.");
        // Every store is explicit. AppServices and the production MainWindow are never created.
        var settings = new AppSettings { ThemeMode = _dark ? "dark" : "light", EulaAcceptedVersion = "2026-09-22", TranscriptFontSize = 18, SkipOpeningPrompts = false, TelemetryEnabled = false };
        var settingsStore = new AppSettingsStore(Path.Combine(isolated, "settings.json")); await settingsStore.SaveAsync(settings);
        var library = Path.Combine(isolated, "library"); var projects = new CourseProjectStore(Path.Combine(isolated, "projects"));
        await using var queue = new TranscriptionQueue((_, _, _, _) => throw new InvalidOperationException("The snapshot fixture cannot submit ASR requests."), _ => Task.FromResult<DuplicateMatch?>(null), new QueueStore(Path.Combine(isolated, "queue.json")), new SrtRecognitionCache(Path.Combine(isolated, "cache")), () => "offscreen-fixture");
        await using var studentVm = new StudentViewModel(() => new FixtureAudioPlayer(), library, new LessonProgressStore(Path.Combine(isolated, "progress.json")), settingsStore, action => Dispatcher.UIThread.Post(action));
        await studentVm.RefreshAsync();
        var student = new StudentView(studentVm);
        var teacher = new TeacherView(projects, queue, () => settings, library);
        var settingsView = new SettingsView(settingsStore); settingsView.Populate(settings);
        var queueView = new QueueView(queue, _ => Task.CompletedTask, pickAudio: () => Task.CompletedTask);
        var shell = CreateShell(student, 0); teacher.ProjectOpened = () => CollapsePane(shell, 1); shell.Show();
        await CaptureAsync(shell, "student-home");
        var host = Find<ContentControl>(shell, "SnapshotPageHost");
        SetDestination(shell, host, teacher, 1); await teacher.RefreshAsync(); await CaptureAsync(shell, "teacher-empty");
        SetDestination(shell, host, settingsView, 2); await settingsView.ReloadAsync(); await CaptureAsync(shell, "settings");
        SetDestination(shell, host, student, 0);
        var taskDialog = new FAContentDialog { Title = "转写任务", Content = queueView, CloseButtonText = "关闭", MaxWidth = Math.Clamp(_width * .618, 640, 980) };
        queueView.Width = Math.Clamp(_width * .618, 640, 980) - 48; queueView.Height = Math.Clamp(_height * .72, 480, 720) - 112;
        var shown = taskDialog.ShowAsync(shell); await CaptureAsync(shell, "queue-empty"); taskDialog.Hide(); await shown;
        var audio = Path.Combine(isolated, "reference.wav"); WriteSilentWave(audio, 8);
        var cues = new SrtCue[] { new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "听下面的录音，回答第1小题。"), new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), "Hello, Emma. How are you today?"), new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), "I am fine. Thank you.") };
        var transcript = SrtParser.Serialize(cues); var exercises = SrtQuestionPlanner.Plan(cues);
        var project = await projects.CreateAsync(audio); project = project with { Title = "示例听力课程", AudioDuration = TimeSpan.FromMinutes(3), Transcript = transcript, Step = CourseProjectStep.Review, Exercises = exercises, AutomaticQuestionPlanApplied = true }; await projects.SaveAsync(project);
        SetDestination(shell, host, teacher, 1); await teacher.OpenProjectAsync(project.Id); await CaptureAsync(shell, "teacher-review");
        SetField(teacher, "_overview", true); Invoke(teacher, "BuildStage"); await CaptureAsync(shell, "teacher-overview");
        await InvokeAsync(teacher, "ChangePhaseAsync", ReviewPhase.Cloze); await CaptureAsync(shell, "teacher-cloze");
        await InvokeAsync(teacher, "FinishReviewAsync"); project = ((TeacherWorkspaceViewModel)GetField(teacher, "_vm")!).Project!; await CaptureAsync(shell, "teacher-completed");
        var blank = await projects.CreateAsync(); await teacher.OpenProjectAsync(blank.Id); await CaptureAsync(shell, "teacher-audio");
        var transcription = await projects.CreateAsync(audio); await teacher.OpenProjectAsync(transcription.Id); await CaptureAsync(shell, "teacher-transcription");
        var package = Path.Combine(isolated, "reference.ilp"); await new ProjectDelivery().CreateIlpAsync(project, package); await new IlpImporter(library).ImportFileAsync(package);
        SetDestination(shell, host, student, 0); await studentVm.RefreshAsync(); studentVm.SelectedLesson = studentVm.Lessons.Single(); await studentVm.CurrentLoad; await CaptureAsync(shell, "student-loaded");
        host.Content = null; shell.Close();
        var owner = CreateWindow(new Grid()); owner.Show();
        var wizardType = AppAssembly.GetType("IL.App.Views.Dialogs.FirstRunWizard", throwOnError: true)!;
        var showWizard = wizardType.GetMethod("ShowAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var initial = new AppSettings { ThemeMode = _dark ? "dark" : "light", CloudApiKey = "" };
        var wizardTask = (Task<AppSettings?>)showWizard.Invoke(null, [owner, initial, "用户协议", "隐私说明"])!;
        var wizard = ((Border)((Grid)owner.Content!).Children.Last()).Child!;
        var names = new[] { "welcome", "agreement", "basics", "appearance", "api", "done" };
        for (var step = 0; step < names.Length; step++)
        {
            SetField(wizard, "_step", step); Invoke(wizard, "Render"); await CaptureAsync(owner, "oobe-" + names[step]);
        }
        var finish = (Action<AppSettings?>)GetField(wizard, "_finish")!; finish(null); await wizardTask; owner.Close();
        await File.WriteAllTextAsync(Path.Combine(_output, "snapshots.json"), JsonSerializer.Serialize(new { Platform = "Avalonia.Headless 12.1.3 + Skia", Size = new { Width = _width, Height = _height }, Theme = _dark ? "dark" : "light", Fixtures = "isolated temporary stores + fixture audio player", Snapshots = Captures }, Json));
    }

    private static Window CreateWindow(Control content) => new() { Width = _width, Height = _height, WindowDecorations = WindowDecorations.None, CanResize = false, Content = content };
    private static TextBlock Icon(string name, double size = 16) => (TextBlock)WorkspaceUi.GetMethod("Icon")!.Invoke(null, [name, size])!;
    private static Window CreateShell(Control view, int destination)
    {
        var expanded = _paneExpanded = _width >= 1200;
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions(expanded ? "320,*" : "48,*") };
        var pane = new Border(); pane.Bind(Border.BackgroundProperty, new DynamicResourceExtension("WorkspacePaneBrush"));
        var navigation = new Grid { RowDefinitions = new RowDefinitions("42,*,Auto"), Margin = new Thickness(4, 0) };
        var toggle = new Button { Content = Icon("global_nav_button"), Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) }; toggle.Classes.Add("subtle"); navigation.Children.Add(toggle);
        var destinations = new StackPanel { Spacing = 4 };
        foreach (var name in new[] { "Student", "Teacher", "Settings" }) { var button = new Button { Name = "Snapshot" + name + "Button" }; button.Classes.Add("nav"); destinations.Children.Add(button); }
        Grid.SetRow(destinations, 1); navigation.Children.Add(destinations);
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(4, 0, 4, 4) }; footer.Children.Add(new Separator()); var tasks = new Button { Name = "SnapshotQueueButton" }; tasks.Classes.Add("nav"); footer.Children.Add(tasks); Grid.SetRow(footer, 2); navigation.Children.Add(footer);
        pane.Child = navigation; root.Children.Add(pane);
        var host = new ContentControl { Name = "SnapshotPageHost", Content = view, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var work = new Border { Child = host, BorderThickness = new Thickness(1, 1, 0, 0), CornerRadius = new CornerRadius(8, 0, 0, 0) }; work.Bind(Border.BackgroundProperty, new DynamicResourceExtension("WorkspaceBackgroundBrush")); work.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("WorkspaceCardBorderBrush")); Grid.SetColumn(work, 1); root.Children.Add(work);
        var window = CreateWindow(root); SetNavigation(window, destination); return window;
    }
    private static T Find<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
    private static void CollapsePane(Window window, int destination)
    {
        _paneExpanded = false; ((Grid)window.Content!).ColumnDefinitions[0].Width = new GridLength(48); SetNavigation(window, destination);
    }
    private static void SetDestination(Window window, ContentControl host, Control page, int destination) { host.Content = page; SetNavigation(window, destination); }
    private static void SetNavigation(Window window, int destination)
    {
        // Use the production navigation rendering method; only the outer grid mirrors MainWindow.axaml.
        var render = typeof(IL.App.MainWindow).GetMethod("NavigationContent", BindingFlags.NonPublic | BindingFlags.Static)!;
        var expanded = _paneExpanded;
        foreach (var (name, icon, text, index) in new[] { ("Student", "play", "播放", 0), ("Teacher", "education", "制作", 1), ("Settings", "settings", "设置", 2), ("Queue", "sync", "转写任务", -1) })
            render.Invoke(null, [Find<Button>(window, "Snapshot" + name + "Button"), icon, text, expanded, destination == index, 0]);
    }
    private static async Task CaptureAsync(Window window, string name)
    {
        // Let the actual control animations reach their settled frame, then drain deferred layout work.
        await Task.Delay(450); Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Skia did not produce a frame for " + name);
        var path = Path.Combine(_output, name + ".png"); frame.Save(path, PngBitmapEncoderOptions.Default);
        Captures.Add(new { Name = name, File = Path.GetFileName(path), PixelWidth = frame.PixelSize.Width, PixelHeight = frame.PixelSize.Height, InheritedFont = window.FontFamily.ToString(), FontSize = window.FontSize }); Console.WriteLine(name + ": " + frame.PixelSize);
    }
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    private static object? GetField(object target, string name) => target.GetType().GetField(name, PrivateInstance)!.GetValue(target);
    private static Task InvokeAsync(object target, string name, params object[] arguments) => (Task)target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, arguments)!;
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, null);
    private sealed record FontResult(string RequestedFamily, string ResolvedFamily, string TypographicFamily, string Weight, string ResolvedWeight, bool Resolved, bool RequiredGlyphsPresent, IReadOnlyDictionary<string, ushort> Glyphs);
    private sealed record FontReport(IReadOnlyList<string> EmbeddedAssets, IReadOnlyList<FontResult> Fonts);
    private static FontReport DiagnoseFonts()
    {
        var assets = AssetLoader.GetAssets(new Uri("avares://IL.App/Assets/Fonts/"), null).Select(u => u.ToString()).Order().ToArray(); var fonts = new List<FontResult>();
        foreach (var (family, weight, samples) in new[] { ("Source Han Sans CN", FontWeight.Normal, new[] { 0x8BFE, 0x7CBE, 0x41 }), ("Source Han Sans CN", FontWeight.Medium, new[] { 0x8BFE, 0x41 }), ("Source Han Sans CN", FontWeight.Bold, new[] { 0x8BFE, 0x41 }), ("Fabric MDL2 Assets", FontWeight.Normal, new[] { 0xE768, 0xE713, 0xED25, 0xE74D }) })
        {
            var typeface = new Typeface(new FontFamily("avares://IL.App/Assets/Fonts#" + family), FontStyle.Normal, weight);
            var resolved = FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs);
            var values = samples.ToDictionary(c => $"U+{c:X4}", c => resolved && glyphs!.CharacterToGlyphMap.TryGetGlyph(c, out var glyph) ? glyph : (ushort)0);
            fonts.Add(new(family, glyphs?.FamilyName ?? "", glyphs?.TypographicFamilyName ?? "", weight.ToString(), glyphs?.Weight.ToString() ?? "", resolved && (glyphs?.FamilyName == family || glyphs?.TypographicFamilyName == family) && glyphs?.Weight == weight, values.Values.All(g => g != 0), values));
        }
        return new(assets, fonts);
    }
    private static void WriteSilentWave(string path, int seconds)
    {
        const int rate = 8000; var samples = rate * seconds;
        using var output = new BinaryWriter(File.Create(path), Encoding.ASCII);
        output.Write(Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + samples * 2); output.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(rate); output.Write(rate * 2); output.Write((short)2); output.Write((short)16); output.Write(Encoding.ASCII.GetBytes("data")); output.Write(samples * 2); output.Write(new byte[samples * 2]);
    }
    private sealed class FixtureAudioPlayer : IAudioPlayer
    {
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public bool IsPlaying { get; private set; }
        public string? ErrorMessage => null;
        public double PlaybackRate { get; set; } = 1;
        public double Volume { get; set; } = 1;
        public event EventHandler? StateChanged;
        public Task OpenAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken ct = default) { IsPlaying = true; StateChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken ct = default) { IsPlaying = false; StateChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { IsPlaying = false; Position = TimeSpan.Zero; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken ct = default) { Position = position; return Task.CompletedTask; }
        public Task SetLoopAsync(TimeSpan? start, TimeSpan? end, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
