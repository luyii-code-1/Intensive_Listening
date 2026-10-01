using IL.Core.Projects;
using IL.Core.Transcription;
using Xunit;

namespace IL.Core.Tests;

public sealed class ProjectTranscriptionBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il2-binding-" + Guid.NewGuid().ToString("N"));
    private const string Srt = "1\n00:00:00,000 --> 00:00:01,000\nHello from transcription.\n";
    private CourseProjectStore Projects => new(Path.Combine(_root, "projects"));
    private QueueStore Jobs => new(Path.Combine(_root, "queue.json"));
    public ProjectTranscriptionBindingTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private TranscriptionQueue Queue(AsrRunner runner) => new(runner, _ => Task.FromResult<DuplicateMatch?>(null), Jobs,
        new SrtRecognitionCache(Path.Combine(_root, "cache")), () => "binding-test");
    private async Task<CourseProject> ProjectAsync()
    {
        var audio = Path.Combine(_root, "input.wav"); await File.WriteAllTextAsync(audio, "audio fixture");
        return await Projects.CreateAsync(audio);
    }

    [Fact]
    public async Task ImmediateCompletionAndCacheHitPersistBeforeAnyProjectIsReopened()
    {
        var calls = 0;
        await using var queue = Queue((_, _, _, _) => { calls++; return Task.FromResult(Srt); });
        await using var binding = new ProjectTranscriptionBinding(Projects, queue);
        var completed = new List<string>(); binding.TranscriptBound += project => completed.Add(project.Id);
        for (var i = 0; i < 2; i++)
        {
            var pending = await binding.EnqueueAsync(await ProjectAsync());
            await queue.WaitForIdleAsync(); await binding.FlushAsync(); await queue.FlushAsync();
            var saved = await Projects.LoadByIdAsync(pending.Id);
            Assert.Equal(Srt, saved!.Transcript); Assert.Equal(CourseProjectStep.Review, saved.Step);
            Assert.Null(saved.TranscriptionJobId); Assert.Equal(ReviewPhase.Grouping, saved.ReviewPhase);
            Assert.True((await Jobs.LoadAsync()).Single(j => j.Id == pending.TranscriptionJobId).SrtConsumed);
        }
        Assert.Equal(1, calls); Assert.Equal(2, completed.Count);
    }

    [Fact]
    public async Task PendingCompletedJobIsBoundOnNextStartup()
    {
        var project = (await ProjectAsync()) with { TranscriptionJobId = "completed-before-exit" };
        await Projects.SaveAsync(project);
        await Jobs.SaveAsync([new("completed-before-exit", project.Title, project.AudioPath!, TranscriptionJobStatus.Completed,
            TranscriptionStage.Formatting, "done", DateTimeOffset.Now, ProjectId: project.Id, Srt: Srt)]);
        await using var queue = Queue((_, _, _, _) => throw new InvalidOperationException("Recovery must not submit ASR."));
        await using var binding = new ProjectTranscriptionBinding(Projects, queue);
        await queue.RestoreAsync(); await binding.RestorePendingAsync();
        Assert.Equal(CourseProjectStep.Review, (await Projects.LoadByIdAsync(project.Id))!.Step);
        Assert.True(queue.JobById(project.TranscriptionJobId)!.SrtConsumed);
    }

    [Fact]
    public async Task OldAudioJobAndEmptyRecognitionDoNotReplaceCurrentProject()
    {
        var project = (await ProjectAsync()) with { TranscriptionJobId = "current-job" }; await Projects.SaveAsync(project);
        await using var queue = Queue((_, _, _, _) => Task.FromResult(""));
        await using var binding = new ProjectTranscriptionBinding(Projects, queue);
        var old = new TranscriptionJob("old-job", project.Title, project.AudioPath!, TranscriptionJobStatus.Completed,
            TranscriptionStage.Formatting, "done", DateTimeOffset.Now, ProjectId: project.Id, Srt: Srt);
        Assert.Null(await binding.BindAsync(old));
        await binding.EnqueueAsync(project); await queue.WaitForIdleAsync(); await binding.FlushAsync();
        var saved = await Projects.LoadByIdAsync(project.Id);
        Assert.Equal(CourseProjectStep.Transcription, saved!.Step); Assert.False(saved.HasTranscript);
    }
}
