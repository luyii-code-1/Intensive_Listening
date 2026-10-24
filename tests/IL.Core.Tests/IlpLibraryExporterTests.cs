using IL.Core.Ilp;
using Xunit;

namespace IL.Core.Tests;

public sealed class IlpLibraryExporterTests
{
    [Fact]
    public async Task ExportedLibraryLessonCanBeImportedWithCurrentMetadataAndMedia()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "source.wav"); var transcript = Path.Combine(root, "source.srt");
            await File.WriteAllBytesAsync(audio, [1, 2, 3, 4]);
            await File.WriteAllTextAsync(transcript, "1\n00:00:00,000 --> 00:00:01,000\nHello world\n");
            var package = Path.Combine(root, "source.ilp");
            await new IlpCreator().CreateAsync("Original", audio, transcript, package, Guid.NewGuid().ToString(), 3);
            var original = await new IlpImporter(Path.Combine(root, "library")).ImportFileAsync(package);
            var edited = original with { Manifest = original.Manifest with { Title = "Current title" } };
            var output = Path.Combine(root, "export.ilp");
            await new IlpLibraryExporter().ExportAsync(edited, output);
            var imported = await new IlpImporter(Path.Combine(root, "restored")).ImportFileAsync(output);
            Assert.Equal("Current title", imported.Manifest.Title);
            Assert.Equal(3, imported.Manifest.PackageVersion);
            Assert.Equal(original.Id, imported.Id);
            Assert.Equal(await File.ReadAllBytesAsync(audio), await File.ReadAllBytesAsync(imported.AudioPath));
            Assert.Equal(original.Cues, imported.Cues);
            await Assert.ThrowsAsync<IOException>(() => new IlpLibraryExporter().ExportAsync(edited, Path.Combine(original.DirectoryPath, "inside.ilp")));
        }
        finally { Directory.Delete(root, true); }
    }
}
