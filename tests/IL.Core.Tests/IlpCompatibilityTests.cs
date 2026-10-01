using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IL.Core.Ilp;
using IL.Core.Models;
using IL.Core.Student;
using Xunit;

namespace IL.Core.Tests;

public sealed class IlpCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il2-compat-" + Guid.NewGuid().ToString("N"));
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
    public IlpCompatibilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ImportsPackageGeneratedByCurrentDartCreator()
    {
        var lesson = await new IlpImporter(_root).ImportFileAsync(Fixture("dart-course.ilp"));
        Assert.Equal("Dart compatibility 兼容课程", lesson.Manifest.Title);
        Assert.Equal(3, lesson.Manifest.PackageVersion);
        Assert.Equal(TimeSpan.FromSeconds(5), lesson.Manifest.Duration);
        Assert.Equal(2, lesson.Cues.Count);
        Assert.Equal("Write down the key details.", lesson.Cues[1].Text);
        Assert.Equal("m1", Assert.Single(lesson.Manifest.Exercises.Materials).Id);
        Assert.Equal(0, Assert.Single(lesson.Manifest.Exercises.Questions).AnswerIndex);
        Assert.Equal(new[] { 3, 4 }, lesson.Manifest.Exercises.ClozeWordIndexes[1]);
        var verified = await new IlpLibrary(_root).LoadByIdAsync(lesson.Id);
        Assert.NotNull(verified);
    }

    [Fact]
    public void ManifestSerializationPreservesDartJson()
    {
        var bytes = File.ReadAllBytes(Fixture("dart-manifest.json"));
        var manifest = IlpManifest.FromBytes(bytes);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bytes), JsonNode.Parse(manifest.ToBytes())));
    }

    [Fact]
    public async Task DuplicateImportAndVersionReplacementPreserveLibrary()
    {
        var importer = new IlpImporter(_root);
        var original = await importer.ImportFileAsync(Fixture("dart-course.ilp"));
        var duplicate = await Assert.ThrowsAsync<IlpException>(() => importer.ImportFileAsync(Fixture("dart-course.ilp")));
        Assert.Equal(IlpError.DuplicatePackage, duplicate.Code);
        var replaced = await importer.ImportFileAsync(Fixture("dart-course.ilp"), true, "Renamed");
        Assert.Equal(original.Id, replaced.Id);
        Assert.Equal("Renamed", replaced.Manifest.Title);
        Assert.Single((await new IlpLibrary(_root).LoadAsync()).Lessons);
    }

    [Fact]
    public async Task HashMismatchDoesNotReplaceInstalledCourse()
    {
        var importer = new IlpImporter(_root);
        var original = await importer.ImportFileAsync(Fixture("dart-course.ilp"));
        var corrupt = Path.Combine(_root, "corrupt.ilp");
        File.Copy(Fixture("dart-course.ilp"), corrupt);
        using (var archive = ZipFile.Open(corrupt, ZipArchiveMode.Update))
        {
            archive.GetEntry("audio.wav")!.Delete();
            using var entry = archive.CreateEntry("audio.wav").Open();
            entry.Write([1, 2, 3]);
        }
        var error = await Assert.ThrowsAsync<IlpException>(() => importer.ImportFileAsync(corrupt, true));
        Assert.Equal(IlpError.HashMismatch, error.Code);
        Assert.NotNull(await new IlpLibrary(_root).LoadByIdAsync(original.Id));
    }

    [Fact]
    public async Task CSharpExportsCompatiblePackage()
    {
        var imported = await new IlpImporter(Path.Combine(_root, "library")).ImportFileAsync(Fixture("dart-course.ilp"));
        var output = Path.Combine(_root, "export.ilp");
        await new IlpCreator().CreateAsync("C# export 兼容课程", imported.AudioPath, imported.TranscriptPath, output,
            imported.Id, 4, exercises: imported.Manifest.Exercises);
        var lesson = await new IlpImporter(Path.Combine(_root, "second")).ImportFileAsync(output);
        Assert.Equal(4, lesson.Manifest.PackageVersion);
        Assert.Equal(2, lesson.Cues.Count);
        Assert.Equal(new[] { 3, 4 }, lesson.Manifest.Exercises.ClozeWordIndexes[1]);
        var requested = Environment.GetEnvironmentVariable("IL2_DART_EXPORT_PATH");
        if (!string.IsNullOrEmpty(requested)) File.Copy(output, requested, overwrite: true);
    }

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("audio.flac")]
    public void RejectsUnsupportedManifestPaths(string audioPath)
    {
        var json = JsonNode.Parse(File.ReadAllBytes(Fixture("dart-manifest.json")))!.AsObject();
        json["audioPath"] = audioPath;
        Assert.Equal(IlpError.InvalidPath, Assert.Throws<IlpException>(() => IlpManifest.FromBytes(Encoding.UTF8.GetBytes(json.ToJsonString()))).Code);
    }

    [Theory]
    [InlineData("1\\n00:00:02,000 --> 00:00:01,000\\nBad")]
    [InlineData("1\\n00:60:00,000 --> 00:60:01,000\\nBad")]
    [InlineData("1\\n00:00:00,000 --> 00:00:06,000\\nBad")]
    public void RejectsInvalidSrtTimes(string source)
    {
        Assert.Equal(IlpError.InvalidTranscript, Assert.Throws<IlpException>(() => SrtParser.Parse(Encoding.UTF8.GetBytes(source.Replace("\\n", "\n")), TimeSpan.FromSeconds(5))).Code);
    }

    [Fact]
    public void SrtHandlesBomNewlinesAndRenumbers()
    {
        var bytes = Encoding.UTF8.GetBytes("\uFEFF8\r\n00:00:00.000 --> 00:00:01.000\r\nHello\r\nworld\r\n");
        var cues = SrtParser.Parse(bytes, TimeSpan.FromSeconds(2));
        Assert.Equal("Hello\nworld", Assert.Single(cues).Text);
        Assert.StartsWith("1\n00:00:00,000", SrtParser.Serialize(cues));
    }

    [Fact]
    public async Task ProgressKeepsDartFieldsAndRevealedCloze()
    {
        var store = new LessonProgressStore(Path.Combine(_root, "lesson_progress.json"));
        var value = new LessonProgress("11111111-1111-4111-8111-111111111111", 2345, DateTimeOffset.Now, new HashSet<string> { "1:4", "1:3" });
        await store.SaveAsync(new Dictionary<string, LessonProgress> { [value.PackageUuid] = value });
        var loaded = (await store.LoadAsync())[value.PackageUuid];
        Assert.Equal(2345, loaded.PositionMs);
        Assert.True(value.RevealedCloze.SetEquals(loaded.RevealedCloze));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "lesson_progress.json")));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("1:3", json.RootElement.GetProperty("records")[0].GetProperty("revealedCloze")[0].GetString());
    }
}
