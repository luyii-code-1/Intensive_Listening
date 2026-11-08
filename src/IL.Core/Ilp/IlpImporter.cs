using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace IL.Core.Ilp;

public sealed class IlpImporter(string libraryDirectory)
{
    public async Task<IlpManifest> ReadManifestAsync(string file, CancellationToken ct = default)
    {
        try
        {
            IlpManifest manifest;
            using (var archive = ZipFile.OpenRead(file))
            {
                await ArchiveSafety.ValidateAsync(archive, 2L * 1024 * 1024 * 1024, ct);
                manifest = await ReadManifestAsync(Entries(archive), ct);
            }
            return manifest.FormatVersion < IlpManifest.SupportedVersion ? await UpgradeFileAsync(file, ct) : manifest;
        }
        catch (InvalidDataException ex) { throw new IlpException(IlpError.CorruptArchive, ".ilp 文件不是有效的 ZIP", ex); }
    }

    public async Task<ImportedLesson> ImportFileAsync(string file, bool replaceExisting = false, string? titleOverride = null, CancellationToken ct = default)
    {
        await ReadManifestAsync(file, ct);
        var root = Path.GetFullPath(libraryDirectory);
        var staging = Path.Combine(root, ".import", "ilp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(file);
            var entries = Entries(archive);
            var manifest = await ReadManifestAsync(entries, ct);
            if (!string.IsNullOrWhiteSpace(titleOverride)) manifest = manifest with { Title = titleOverride.Trim() };
            IlpManifest.FromBytes(manifest.ToBytes());
            foreach (var name in new[] { manifest.AudioPath, manifest.TranscriptPath })
            {
                var entry = Required(entries, name);
                await using var input = ArchiveSafety.OpenRead(entry, name == manifest.TranscriptPath ? 2 * 1024 * 1024 : 2L * 1024 * 1024 * 1024);
                await using var output = new FileStream(Path.Combine(staging, name), FileMode.CreateNew);
                await input.CopyToAsync(output, ct);
            }
            await FileHashing.VerifySha256Async(Path.Combine(staging, manifest.AudioPath), manifest.AudioSha256, ct);
            await FileHashing.VerifySha256Async(Path.Combine(staging, manifest.TranscriptPath), manifest.TranscriptSha256, ct);
            var cues = SrtParser.Parse(await File.ReadAllBytesAsync(Path.Combine(staging, manifest.TranscriptPath), ct), manifest.Duration);
            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.json"), manifest.ToBytes(), ct);
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(root, manifest.PackageUuid);
            var backup = destination + "." + Guid.NewGuid().ToString("N") + ".bak";
            var replacing = Directory.Exists(destination);
            if (replacing && !replaceExisting) throw new IlpException(IlpError.DuplicatePackage, "该精听包已经导入");
            if (replacing) Directory.Move(destination, backup);
            try { Directory.Move(staging, destination); }
            catch
            {
                if (replacing) Directory.Move(backup, destination);
                throw;
            }
            if (replacing)
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return new(manifest.PackageUuid, destination, manifest, cues);
        }
        catch (InvalidDataException ex) { throw new IlpException(IlpError.CorruptArchive, ".ilp 文件不是有效的 ZIP", ex); }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static async Task<IlpManifest> UpgradeFileAsync(string file, CancellationToken ct)
    {
        var temporary = Path.GetFullPath(file) + "." + Guid.NewGuid().ToString("N") + ".upgrade";
        try
        {
            var upgraded = await Task.Run(async () =>
            {
                using var source = ZipFile.OpenRead(file);
                var entries = Entries(source);
                var manifest = await ReadManifestAsync(entries, ct);
                foreach (var (name, expected) in new[] { (manifest.AudioPath, manifest.AudioSha256), (manifest.TranscriptPath, manifest.TranscriptSha256) })
                {
                    await using var input = ArchiveSafety.OpenRead(Required(entries, name), name == manifest.TranscriptPath ? 2 * 1024 * 1024 : 2L * 1024 * 1024 * 1024);
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                    if (actual != expected) throw new IlpException(IlpError.HashMismatch, "升级前完整性校验失败");
                }
                byte[] transcript;
                await using (var input = ArchiveSafety.OpenRead(Required(entries, manifest.TranscriptPath), 2 * 1024 * 1024))
                { using var output = new MemoryStream(); await input.CopyToAsync(output, ct); transcript = output.ToArray(); }
                SrtParser.Parse(transcript, manifest.Duration);
                JsonObject json;
                await using (var input = ArchiveSafety.OpenRead(Required(entries, "manifest.json"), 256 * 1024))
                    json = (await JsonNode.ParseAsync(input, cancellationToken: ct))!.AsObject();
                var result = manifest with { FormatVersion = IlpManifest.SupportedVersion };
                json["formatVersion"] = result.FormatVersion;
                json["creatorMachine"] = result.CreatorMachine;
                json["creatorAgent"] = result.CreatorAgent;
                await using var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                using var archive = new ZipArchive(target, ZipArchiveMode.Create, true);
                foreach (var entry in source.Entries)
                {
                    var replacement = archive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    replacement.ExternalAttributes = entry.ExternalAttributes;
                    replacement.LastWriteTime = entry.LastWriteTime;
                    await using var output = replacement.Open();
                    if (entry.FullName == "manifest.json") await System.Text.Json.JsonSerializer.SerializeAsync(output, json, cancellationToken: ct);
                    else
                    {
                        await using var input = ArchiveSafety.OpenRead(entry, entry.FullName == manifest.TranscriptPath ? 2 * 1024 * 1024 : 2L * 1024 * 1024 * 1024);
                        await input.CopyToAsync(output, ct);
                    }
                }
                return result;
            }, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, Path.GetFullPath(file), overwrite: true);
            return upgraded;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static Dictionary<string, ZipArchiveEntry> Entries(ZipArchive archive)
    {
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
            if (!result.TryAdd(entry.FullName, entry)) throw new IlpException(IlpError.DuplicateEntry, $"ZIP 内存在重复条目：{entry.FullName}");
        return result;
    }
    private static ZipArchiveEntry Required(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string name)
    {
        if (!entries.TryGetValue(name, out var entry) || entry.Name.Length == 0) throw new IlpException(IlpError.MissingFile, $"精听包缺少文件：{name}");
        if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new IlpException(IlpError.SymbolicLink, $"ZIP 包含符号链接：{name}");
        return entry;
    }
    private static async Task<IlpManifest> ReadManifestAsync(IReadOnlyDictionary<string, ZipArchiveEntry> entries, CancellationToken ct)
    {
        var entry = Required(entries, "manifest.json");
        if (entry.Length > 256 * 1024) throw new IlpException(IlpError.InvalidManifest, "manifest.json 过大");
        await using var input = ArchiveSafety.OpenRead(entry, 256 * 1024);
        using var output = new MemoryStream();
        await input.CopyToAsync(output, ct);
        return IlpManifest.FromBytes(output.ToArray());
    }
}
