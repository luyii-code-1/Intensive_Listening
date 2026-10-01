using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IL.App.ViewModels;
using IL.App.Views;
using IL.Core.Ilp;
using IL.Core.Models;
using IL.Core.Projects;
using IL.Core.Settings;
using IL.Core.Student;
using IL.Core.Transcription;

namespace IL.UiSnapshots;

internal static partial class Program
{
    private static async Task ProbeAsync(string isolated)
    {
        var settings = new AppSettings { EulaAcceptedVersion = "2026-09-22", SkipOpeningPrompts = false, TelemetryEnabled = false };
        var store = new AppSettingsStore(Path.Combine(isolated, "settings.json")); await store.SaveAsync(settings);
        var projects = new CourseProjectStore(Path.Combine(isolated, "projects"));
        var library = Path.Combine(isolated, "library");
        var audio = Path.Combine(isolated, "reference.wav"); WriteSilentWave(audio, 8);
        var cues = Enumerable.Range(0, 250).Select(i => new SrtCue(TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds(i * 3 + 3), i % 25 == 0 ? $"听下面的录音，回答第{i / 25 + 1}小题。" : "Hello Emma how are you today I thought your book launch was due on the last day of May and so did I.")).ToArray();
        var project = await projects.CreateAsync(audio);
        project = project with { Title = "250 句性能样本", Transcript = SrtParser.Serialize(cues), AudioDuration = TimeSpan.FromSeconds(750), Step = CourseProjectStep.Review, Exercises = SrtQuestionPlanner.Plan(cues), AutomaticQuestionPlanApplied = true };
        await projects.SaveAsync(project);
        var package = Path.Combine(isolated, "reference.ilp"); await new ProjectDelivery().CreateIlpAsync(project, package);
        var lesson = await new IlpImporter(library).ImportFileAsync(package);
        await using var vm = new StudentViewModel(() => new FixtureAudioPlayer(), library, new LessonProgressStore(Path.Combine(isolated, "progress.json")), store, action => Dispatcher.UIThread.Post(action));
        var student = new StudentView(vm); var shell = CreateShell(student, 0); shell.Show();
        await Task.Delay(150);
        var clock = Stopwatch.StartNew(); var allocations = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++) vm.Lessons.Add(new(lesson with { Id = "fixture-" + i }));
        var homeMs = clock.Elapsed.TotalMilliseconds; var homeBytes = GC.GetAllocatedBytesForCurrentThread() - allocations;
        await Task.Delay(150);
        SetField(vm, "_lesson", lesson); SetField(vm, "_audioPath", lesson.AudioPath); vm.Title = project.Title; vm.DurationSeconds = 750;
        foreach (var (cue, i) in cues.Select((c, i) => (c, i))) vm.Cues.Add(new(i, cue));
        vm.ActiveCue = vm.Cues[1];
        allocations = GC.GetAllocatedBytesForCurrentThread(); clock.Restart(); Invoke(student, "ShowPage");
        var openMs = clock.Elapsed.TotalMilliseconds; var openBytes = GC.GetAllocatedBytesForCurrentThread() - allocations;
        await CaptureAsync(shell, "performance-student");
        var renderedRows = ((System.Collections.IDictionary)GetField(student, "_cueRows")!).Count;
        var controls = student.GetVisualDescendants().Count();
        var transcript = (ScrollViewer)GetField(student, "_transcript")!;
        vm.FollowTranscript = false; transcript.Offset = new Avalonia.Vector(0, transcript.Extent.Height * .6); await Task.Delay(450);
        var scrolledRows = ((System.Collections.IDictionary)GetField(student, "_cueRows")!).Count;
        if (renderedRows is <= 0 or >= 80 || scrolledRows is <= 0 or >= 80) throw new InvalidOperationException("Student transcript realization is not bounded by its viewport.");
        vm.ActiveCue = vm.Cues[224]; vm.FollowTranscript = true; await Task.Delay(100); Invoke(student, "FollowActiveCue"); await Task.Delay(550);
        if (!((System.Collections.IDictionary)GetField(student, "_cueRows")!).Contains(224)) throw new InvalidOperationException($"Follow navigation did not realize the target cue. Offset {transcript.Offset}, extent {transcript.Extent}, rows {string.Join(",", ((System.Collections.IDictionary)GetField(student, "_cueRows")!).Keys.Cast<int>())}");
        await CaptureAsync(shell, "performance-student-jump");
        await using var queue = new TranscriptionQueue((_, _, _, _) => throw new InvalidOperationException(), _ => Task.FromResult<DuplicateMatch?>(null), new QueueStore(Path.Combine(isolated, "queue.json")), new SrtRecognitionCache(Path.Combine(isolated, "cache")), () => "performance-fixture");
        var teacher = new TeacherView(projects, queue, () => settings, library); var host = Find<ContentControl>(shell, "SnapshotPageHost"); host.Content = teacher;
        clock.Restart(); await teacher.OpenProjectAsync(project.Id); var teacherMs = clock.Elapsed.TotalMilliseconds;
        await CaptureAsync(shell, "performance-teacher");
        var list = (ListBox?)GetField(teacher, "_reviewList"); var teacherRows = list?.GetRealizedContainers().Count();
        if (teacherRows is null or <= 0 or >= 80) throw new InvalidOperationException("Teacher transcript realization is not bounded by its viewport.");
        var report = new { FixtureCues = cues.Length, LibraryItems = 50, HomeBatchMilliseconds = homeMs, HomeBatchAllocatedBytes = homeBytes, StudentBuildMilliseconds = openMs, StudentBuildAllocatedBytes = openBytes, StudentRealizedCues = renderedRows, StudentVisualCount = controls, StudentRealizedAfterScroll = scrolledRows, TeacherOpenMilliseconds = teacherMs, TeacherRealizedRows = teacherRows, Boundary = "Offscreen Skia UI construction and realization, not native platform FPS" };
        await File.WriteAllTextAsync(Path.Combine(_output, "performance.json"), JsonSerializer.Serialize(report, Json)); Console.WriteLine(JsonSerializer.Serialize(report, Json));
        host.Content = null; shell.Close();
    }
}
