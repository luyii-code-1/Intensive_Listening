using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using IL.Core.Ilp;
using IL.Core.Mcp;
using IL.Core.Projects;
using IL.Core.Settings;
using Xunit;

namespace IL.Core.Tests;

public sealed class IlpUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il2-upgrade-" + Guid.NewGuid().ToString("N"));
    public IlpUpgradeTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string LegacyPackage()
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ilp");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "dart-course.ilp"), path);
        return path;
    }
    private static Dictionary<string, byte[]> Payload(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => e.FullName != "manifest.json").ToDictionary(e => e.FullName, e =>
        { using var input = e.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); });
    }
    [Fact]
    public async Task OpeningLegacyPackageUpgradesSourceAndKeepsPayloadAndPackageVersion()
    {
        var path = LegacyPackage(); var before = Payload(path);
        var importer = new IlpImporter(Path.Combine(_root, "library"));
        var manifest = await importer.ReadManifestAsync(path);
        Assert.Equal(3, manifest.FormatVersion); Assert.Equal(3, manifest.PackageVersion);
        Assert.Equal("未知", manifest.CreatorMachine); Assert.Equal("未知", manifest.CreatorAgent);
        var after = Payload(path);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var name in before.Keys) Assert.Equal(before[name], after[name]);
        Assert.Equal(manifest.ToBytes(), (await importer.ReadManifestAsync(path)).ToBytes());
        Assert.Equal(3, (await importer.ImportFileAsync(path)).Manifest.FormatVersion);
    }
    [Fact]
    public async Task CorruptLegacyPackageRemainsUnchangedWhenUpgradeFails()
    {
        var path = LegacyPackage();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("audio.wav")!.Delete();
            using var output = zip.CreateEntry("audio.wav").Open(); output.Write([1, 2, 3]);
        }
        var before = await File.ReadAllBytesAsync(path);
        var error = await Assert.ThrowsAsync<IlpException>(() => new IlpImporter(_root).ReadManifestAsync(path));
        Assert.Equal(IlpError.HashMismatch, error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_root, "*.upgrade"));
    }
    [Fact]
    public async Task AgentAttributionPersistsAndUserEditsKeepUserAttributionOnLaterAgentChanges()
    {
        var store = new CourseProjectStore(Path.Combine(_root, "projects"));
        var api = new AppPrivateApiService(store) { AgentName = () => "Registered Test Agent" };
        var created = await api.DispatchAsync("projects.create", new JsonObject { ["title"] = "Author test" });
        var id = JsonSerializer.SerializeToElement(created).GetProperty("id").GetString()!;
        var project = (await store.LoadByIdAsync(id))!;
        Assert.Equal("Registered Test Agent", project.CreatorAgent);
        Assert.Equal(Environment.MachineName, project.CreatorMachine);
        await store.SaveAsync(project with { CreatorAgent = "用户", UserEdited = true });
        var text = Path.Combine(_root, "exam.txt"); await File.WriteAllTextAsync(text, "Edited exam");
        await api.DispatchAsync("projects.importText", new JsonObject { ["projectId"] = id, ["role"] = "exam", ["textPath"] = text });
        Assert.Equal("用户", (await store.LoadByIdAsync(id))!.CreatorAgent);
    }
    [Fact]
    public async Task ExportContainsProjectCreatorInformation()
    {
        var lesson = await new IlpImporter(Path.Combine(_root, "library")).ImportFileAsync(LegacyPackage());
        var store = new CourseProjectStore(Path.Combine(_root, "projects"));
        var project = await store.CreateAsync(lesson.AudioPath);
        project = project with { Transcript = await File.ReadAllTextAsync(lesson.TranscriptPath), CreatorAgent = "Test Agent", CreatorMachine = "Author PC" };
        var output = await new ProjectDelivery().CreateIlpAsync(project, Path.Combine(_root, "export.ilp"));
        var manifest = await new IlpImporter(_root).ReadManifestAsync(output);
        Assert.Equal(3, manifest.FormatVersion);
        Assert.Equal("Test Agent", manifest.CreatorAgent); Assert.Equal("Author PC", manifest.CreatorMachine);
    }
    [Fact]
    public void AudioProjectPreferenceDefaultsOnAndPersistsOff()
    {
        Assert.True(AppSettings.FromJson(new JsonObject()).CreateProjectOnAudioImport);
        Assert.False(AppSettings.FromJson(new AppSettings { CreateProjectOnAudioImport = false }.ToJson()).CreateProjectOnAudioImport);
    }
}
