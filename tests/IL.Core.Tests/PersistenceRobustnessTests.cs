using System.IO.Compression;
using System.Text.Json.Nodes;
using IL.Core.Projects;
using IL.Core.Transcription;
using Xunit;

namespace IL.Core.Tests;

public sealed class PersistenceRobustnessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "il-persistence-" + Guid.NewGuid().ToString("N"));
    public PersistenceRobustnessTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task FailedAudioReplacementRestoresProjectAndOriginalAudio()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new CourseProjectStore(Path.Combine(root, "projects"));
        var source = Path.Combine(root, "source.wav"); await File.WriteAllTextAsync(source, "old audio");
        var original = (await store.CreateAsync(source)) with { Title = "old title", Transcript = "old transcript" };
        await store.SaveAsync(original);
        var metadata = Path.Combine(store.RootDirectory, original.Id, "project.json");
        var before = await File.ReadAllBytesAsync(metadata);
        var replacement = Path.Combine(root, "replacement.wav"); await File.WriteAllTextAsync(replacement, "new audio");
        using (var held = new FileStream(original.AudioPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => store.BindAudioAsync(original, replacement));
            Assert.Equal(before, await File.ReadAllBytesAsync(metadata));
            Assert.Equal("old transcript", await File.ReadAllTextAsync(Path.Combine(store.RootDirectory, original.Id, "transcript.srt")));
            Assert.Equal("old audio", await File.ReadAllTextAsync(original.AudioPath!));
        }
    }

    [Fact]
    public async Task MetadataReplacementFailureRollsBackTranscript()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new CourseProjectStore(Path.Combine(root, "projects"));
        var original = (await store.CreateAsync()) with { Title = "original", Transcript = "old transcript" };
        await store.SaveAsync(original);
        var directory = Path.Combine(store.RootDirectory, original.Id);
        var metadata = Path.Combine(directory, "project.json");
        var bytes = await File.ReadAllBytesAsync(metadata);
        using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(original with { Title = "new", Transcript = "new transcript" }));
            Assert.Equal("old transcript", await File.ReadAllTextAsync(Path.Combine(directory, "transcript.srt")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(metadata));
        }
        Assert.Equal("old transcript", (await store.LoadByIdAsync(original.Id))!.Transcript);
    }
    [Fact]
    public async Task InterruptedProjectSaveRecoversOnNextLoad()
    {
        var store = new CourseProjectStore(Path.Combine(root, "projects"));
        var original = (await store.CreateAsync()) with { Transcript = "old transcript" };
        await store.SaveAsync(original);
        var directory = Path.Combine(store.RootDirectory, original.Id);
        var transaction = Path.Combine(directory, ".project-save"); Directory.CreateDirectory(transaction);
        File.Move(Path.Combine(directory, "transcript.srt"), Path.Combine(transaction, "0.old"));
        await File.WriteAllTextAsync(Path.Combine(directory, "transcript.srt"), "partially committed transcript");
        await File.WriteAllTextAsync(Path.Combine(transaction, "journal.json"), """
            [{"path":"transcript.srt","hadOriginal":true,"replacement":true},
             {"path":"project.json","hadOriginal":true,"replacement":true}]
            """);
        Assert.Equal("old transcript", (await store.LoadByIdAsync(original.Id))!.Transcript);
        Assert.False(Directory.Exists(transaction));
    }

    [Fact]
    public async Task FailedExportPreservesTheExistingArchive()
    {
        var store = new CourseProjectStore(Path.Combine(root, "projects"));
        var project = await store.CreateAsync();
        Directory.Delete(Path.Combine(store.RootDirectory, project.Id), true);
        var output = Path.Combine(root, "saved.zip");
        await File.WriteAllTextAsync(output, "previous archive");
        await Assert.ThrowsAsync<CourseProjectArchiveException>(() => store.ExportZipToFileAsync(project, output));
        Assert.Equal("previous archive", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task ExportWithinTheProjectDoesNotEmbedTheOutputOrTemporaryArchive()
    {
        var store = new CourseProjectStore(Path.Combine(root, "projects"));
        var project = await store.CreateAsync();
        var output = Path.Combine(store.RootDirectory, project.Id, "saved.zip");
        await File.WriteAllTextAsync(output, "previous archive");
        await store.ExportZipToFileAsync(project, output);
        using var zip = ZipFile.OpenRead(output);
        Assert.Contains(zip.Entries, entry => entry.FullName == "project/project.json");
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("saved.zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExcessiveProjectDurationDoesNotBlockLoadingOtherProjects()
    {
        var store = new CourseProjectStore(Path.Combine(root, "projects"));
        var first = await store.CreateAsync(); await store.CreateAsync();
        var metadata = Path.Combine(store.RootDirectory, first.Id, "project.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
        json["audioDurationMs"] = double.MaxValue;
        await File.WriteAllTextAsync(metadata, json.ToJsonString());
        Assert.Equal(2, (await store.LoadAllAsync()).Count);
        Assert.Null((await store.LoadByIdAsync(first.Id))!.AudioDuration);
    }

    [Fact]
    public async Task QueueRestoresOtherJobsAndInterruptsUnknownStates()
    {
        var file = Path.Combine(root, "queue.json");
        await File.WriteAllTextAsync(file, """
            {"version":1,"jobs":[
              {"id":"first","audioPath":"a.wav","status":"running"},
              {"id":"second","audioPath":"b.wav","status":"999","audioDurationMs":1e999},
              {"id":"first","audioPath":"duplicate.wav","status":"queued"}
            ]}
            """);
        var jobs = await new QueueStore(file).LoadAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Equal(TranscriptionJobStatus.Interrupted, jobs[1].Status);
        Assert.Null(jobs[1].AudioDuration);
    }

    [Fact]
    public async Task QueuePreservesSettledCreditsAcrossRestart()
    {
        var store = new QueueStore(Path.Combine(root, "queue.json"));
        await store.SaveAsync([new("id", "audio", "a.wav", TranscriptionJobStatus.Completed, TranscriptionStage.Formatting, "完成", DateTimeOffset.Now, ChargedCredits: 1.5m)]);
        Assert.Equal(1.5m, Assert.Single(await store.LoadAsync()).ChargedCredits);
    }
}
