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
