namespace IL.Core.Ilp;

public sealed record LibraryEntryIssue(string Id, string Message);
public sealed record LibraryLoadResult(IReadOnlyList<ImportedLesson> Lessons, IReadOnlyList<LibraryEntryIssue> Issues);
public sealed record LessonRef(string Id, string DirectoryPath, IlpManifest Manifest);

public sealed class IlpLibrary(string directory)
{
    public async Task<IReadOnlyList<LessonRef>> ScanManifestsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return [];
        var found = new List<LessonRef>();
        foreach (var path in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(path).StartsWith('.') || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            try { found.Add(new(Path.GetFileName(path), path, IlpManifest.FromBytes(await File.ReadAllBytesAsync(Path.Combine(path, "manifest.json"), ct)))); }
            catch (IlpException) { }
            catch (IOException) { }
        }
        return found.OrderBy(x => x.Manifest.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task<LessonRef?> FindByAudioSha256Async(string hash, CancellationToken ct = default) => (await ScanManifestsAsync(ct)).FirstOrDefault(x => x.Manifest.AudioSha256 == hash);
    public async Task<LessonRef?> FindByUuidAsync(string uuid, CancellationToken ct = default) => (await ScanManifestsAsync(ct)).FirstOrDefault(x => x.Manifest.PackageUuid == uuid);
    public async Task<LessonRef?> FindByTitleAsync(string title, CancellationToken ct = default) => (await ScanManifestsAsync(ct)).FirstOrDefault(x => string.Equals(x.Manifest.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
    public Task RemoveAsync(string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("课程 ID 无效", nameof(id));
        var path = Path.Combine(directory, id);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }
    public async Task<LibraryLoadResult> LoadAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return new([], []);
        var lessons = new List<ImportedLesson>();
        var issues = new List<LibraryEntryIssue>();
        foreach (var path in Directory.EnumerateDirectories(directory))
        {
            var id = Path.GetFileName(path);
            if (id.StartsWith('.') || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            try { lessons.Add(await ReadLessonAsync(id, path, false, ct)); }
            catch (IlpException ex) { issues.Add(new(id, ex.Message)); }
            catch (IOException) { issues.Add(new(id, "课程文件无法读取")); }
        }
        return new(lessons.OrderBy(x => x.Manifest.Title, StringComparer.OrdinalIgnoreCase).ToArray(), issues);
    }
    public async Task<ImportedLesson?> LoadByIdAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(id, "D", out _)) return null;
        var path = Path.Combine(directory, id);
        if (!Directory.Exists(path)) return null;
        try { return await ReadLessonAsync(id, path, true, ct); }
        catch (IlpException) { return null; }
        catch (IOException) { return null; }
    }
    private static async Task<ImportedLesson> ReadLessonAsync(string id, string path, bool verifyAudio, CancellationToken ct)
    {
        var manifestPath = Path.Combine(path, "manifest.json");
        if (!File.Exists(manifestPath)) throw new IlpException(IlpError.MissingFile, "课程缺少 manifest.json");
        var manifest = IlpManifest.FromBytes(await File.ReadAllBytesAsync(manifestPath, ct));
        var audio = Path.Combine(path, manifest.AudioPath);
        var transcript = Path.Combine(path, manifest.TranscriptPath);
        if (!File.Exists(audio) || !File.Exists(transcript)) throw new IlpException(IlpError.MissingFile, "课程音频或字幕文件缺失");
        if (verifyAudio) await FileHashing.VerifySha256Async(audio, manifest.AudioSha256, ct);
        await FileHashing.VerifySha256Async(transcript, manifest.TranscriptSha256, ct);
        return new(id, path, manifest, SrtParser.Parse(await File.ReadAllBytesAsync(transcript, ct), manifest.Duration));
    }
}
