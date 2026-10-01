using System.IO.Compression;
using IL.Core.Models;

namespace IL.Core.Ilp;

public sealed class IlpCreator
{
    public async Task<IlpManifest> CreateAsync(string title, string audioFile, string transcriptFile, string outputFile,
        string packageUuid, int packageVersion, TimeSpan? audioDuration = null, LessonExercises? exercises = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new IlpException(IlpError.InvalidManifest, "请输入课程标题");
        if (!File.Exists(audioFile) || !File.Exists(transcriptFile)) throw new IlpException(IlpError.MissingFile, "请选择音频与 SRT 文件");
        var extension = Path.GetExtension(audioFile).TrimStart('.').ToLowerInvariant();
        if (!IlpManifest.SupportedAudioExtensions.Contains(extension)) throw new IlpException(IlpError.InvalidPath, "音频格式不在当前支持范围内");
        var cues = SrtParser.Parse(await File.ReadAllBytesAsync(transcriptFile, ct), TimeSpan.FromDays(7));
        var duration = audioDuration > cues[^1].End ? audioDuration.Value : cues[^1].End;
        var manifest = new IlpManifest(2, title.Trim(), duration, $"audio.{extension}", "transcript.srt",
            await FileHashing.Sha256Async(audioFile, ct), await FileHashing.Sha256Async(transcriptFile, ct),
            packageUuid, packageVersion, exercises ?? LessonExercises.Empty);
        var manifestBytes = manifest.ToBytes();
        IlpManifest.FromBytes(manifestBytes);
        var destination = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                await using (var entry = zip.CreateEntry("manifest.json").Open()) await entry.WriteAsync(manifestBytes, ct);
                foreach (var (source, name) in new[] { (audioFile, manifest.AudioPath), (transcriptFile, manifest.TranscriptPath) })
                {
                    await using var input = File.OpenRead(source);
                    await using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    await input.CopyToAsync(entry, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return manifest;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
