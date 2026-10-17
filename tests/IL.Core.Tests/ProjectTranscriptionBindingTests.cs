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

    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:01,000\nInvalid duration.\n")]
    [InlineData("1\n00:00:00,000 --> 00:00:03,000\nBeyond audio.\n")]
    public async Task InvalidPendingResultPreservesProjectAndDoesNotBlockOtherRecovery(string invalid)
    {
        var store = Projects;
        var bad = (await ProjectAsync()) with { TranscriptionJobId = "bad", Transcript = Srt, AudioDuration = TimeSpan.FromSeconds(2) };
        var good = (await ProjectAsync()) with { TranscriptionJobId = "good" };
        await store.SaveAsync(bad); await store.SaveAsync(good);
        var metadataPath = Path.Combine(store.RootDirectory, bad.Id, "project.json");
        var transcriptPath = Path.Combine(store.RootDirectory, bad.Id, "transcript.srt");
        var metadata = await File.ReadAllTextAsync(metadataPath);
        await Jobs.SaveAsync([
            new("bad", bad.Title, bad.AudioPath!, TranscriptionJobStatus.Completed, TranscriptionStage.Formatting, "done", DateTimeOffset.Now, ProjectId: bad.Id, Srt: invalid),
            new("good", good.Title, good.AudioPath!, TranscriptionJobStatus.Completed, TranscriptionStage.Formatting, "done", DateTimeOffset.Now, ProjectId: good.Id, Srt: Srt)
        ]);
        await using (var queue = Queue((_, _, _, _) => throw new InvalidOperationException("Recovery must not submit ASR.")))
        {
            await using var binding = new ProjectTranscriptionBinding(store, queue);
            await queue.RestoreAsync(); await binding.RestorePendingAsync(); await queue.FlushAsync();
            Assert.Equal(metadata, await File.ReadAllTextAsync(metadataPath));
            Assert.Equal(Srt, await File.ReadAllTextAsync(transcriptPath));
            Assert.Equal(2, (await store.LoadAllAsync()).Count);
            Assert.Equal(Srt, (await store.LoadByIdAsync(good.Id))!.Transcript);
            Assert.Equal(TranscriptionJobStatus.Failed, queue.JobById("bad")!.Status);
            Assert.Equal(invalid, queue.JobById("bad")!.Srt);
            Assert.False(queue.JobById("bad")!.SrtConsumed);
        }
        await using var restored = Queue((_, _, _, _) => throw new InvalidOperationException("Recovery must not submit ASR."));
        await using var recovery = new ProjectTranscriptionBinding(store, restored);
        await restored.RestoreAsync(); await recovery.RestorePendingAsync();
        Assert.Equal(metadata, await File.ReadAllTextAsync(metadataPath));
        Assert.Equal(Srt, await File.ReadAllTextAsync(transcriptPath));
        Assert.Equal(invalid, restored.JobById("bad")!.Srt);
    }

    [Fact]
    public async Task ReTranscriptionAfterInvalidResultRunsAsrInsteadOfReusingInvalidCache()
    {
        const string invalid = "1\n00:00:01,000 --> 00:00:01,000\nInvalid duration.\n";
        var calls = 0;
        await using var queue = Queue((_, _, _, _) => Task.FromResult(++calls == 1 ? invalid : Srt));
        var store = Projects;
        await using var binding = new ProjectTranscriptionBinding(store, queue);
        var project = await binding.EnqueueAsync(await ProjectAsync());
        await queue.WaitForIdleAsync(); await binding.FlushAsync();
        Assert.Equal(TranscriptionJobStatus.Failed, queue.JobById(project.TranscriptionJobId!)!.Status);
        Assert.False((await store.LoadByIdAsync(project.Id))!.HasTranscript);
        await binding.EnqueueAsync((await store.LoadByIdAsync(project.Id))!);
        await queue.WaitForIdleAsync(); await binding.FlushAsync();
        Assert.Equal(2, calls);
        Assert.Equal(Srt, (await store.LoadByIdAsync(project.Id))!.Transcript);
    }

    [Fact]
    public async Task RecoveryValidatesAgainstActualAudioDurationInsteadOfStaleMetadata()
    {
        var store = Projects;
        var project = (await ProjectAsync()) with { TranscriptionJobId = "stale-duration", AudioDuration = TimeSpan.FromMilliseconds(500) };
        await store.SaveAsync(project);
        await Jobs.SaveAsync([new("stale-duration", project.Title, project.AudioPath!, TranscriptionJobStatus.Completed,
            TranscriptionStage.Formatting, "done", DateTimeOffset.Now, ProjectId: project.Id, Srt: Srt)]);
        await using var queue = Queue((_, _, _, _) => throw new InvalidOperationException("Recovery must not submit ASR."));
        await using var binding = new ProjectTranscriptionBinding(store, queue, _ => Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(2)));
        await queue.RestoreAsync(); await binding.RestorePendingAsync();
        var restored = await store.LoadByIdAsync(project.Id);
        Assert.Equal(TimeSpan.FromSeconds(2), restored!.AudioDuration);
        Assert.Equal(Srt, restored.Transcript);
        Assert.Equal(CourseProjectStep.Review, restored.Step);
        Assert.True(queue.JobById("stale-duration")!.SrtConsumed);
    }
}
