using System.IO.Compression;

namespace IL.Core.Ilp;

public sealed class IlpLibraryExporter
{
    public async Task ExportAsync(ImportedLesson lesson, string outputFile, CancellationToken ct = default)
    {
        var destination = Path.GetFullPath(outputFile);
        var source = Path.GetFullPath(lesson.DirectoryPath) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(source, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("请选择课库目录之外的导出位置");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                await using (var manifest = archive.CreateEntry("manifest.json").Open())
                    await manifest.WriteAsync(lesson.Manifest.ToBytes(), ct);
                foreach (var name in new[] { lesson.Manifest.AudioPath, lesson.Manifest.TranscriptPath })
                {
                    await using var input = File.OpenRead(Path.Combine(lesson.DirectoryPath, name));
                    await using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                    await input.CopyToAsync(entry, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
