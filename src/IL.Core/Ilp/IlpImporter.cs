using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace IL.Core.Ilp;

public sealed class IlpImporter(string libraryDirectory)
{
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaximumTranscriptBytes = 2 * 1024 * 1024;
    private static readonly SemaphoreSlim InstallGate = new(1);

    public Task<IlpManifest> ReadManifestAsync(string file, CancellationToken ct = default) =>
        InspectManifestAsync(file, validatePayload: true, ct);

    public Task<IlpManifest> PreviewManifestAsync(string file, CancellationToken ct = default) =>
        InspectManifestAsync(file, validatePayload: false, ct);

    private static async Task<IlpManifest> InspectManifestAsync(string file, bool validatePayload, CancellationToken ct)
    {
        try
        {
            IlpManifest manifest;
            using (var archive = ZipFile.OpenRead(file))
            {
                manifest = await ReadManifestAsync(Entries(archive), ct);
                if (validatePayload || manifest.FormatVersion < IlpManifest.SupportedVersion)
                    await ArchiveSafety.ValidateAsync(archive, MaximumPackageBytes, ct);
            }
            return manifest.FormatVersion < IlpManifest.SupportedVersion ? await UpgradeFileAsync(file, ct) : manifest;
        }
        catch (InvalidDataException ex) { throw new IlpException(IlpError.CorruptArchive, ".ilp 文件不是有效的 ZIP", ex); }
    }

    public async Task<ImportedLesson> ImportFileAsync(string file, bool replaceExisting = false, string? titleOverride = null, CancellationToken ct = default, string? expectedExistingFingerprint = null)
    {
        await PreviewManifestAsync(file, ct);
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
            await ExtractPayloadAsync(entries, manifest, staging, ct);
            var cues = SrtParser.Parse(await File.ReadAllBytesAsync(Path.Combine(staging, manifest.TranscriptPath), ct), manifest.Duration);
            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.json"), manifest.ToBytes(), ct);
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(root, manifest.PackageUuid);
            var backup = destination + "." + Guid.NewGuid().ToString("N") + ".bak";
            await InstallGate.WaitAsync(ct);
            try
            {
                var replacing = Directory.Exists(destination);
                if (replacing && !replaceExisting) throw new IlpException(IlpError.DuplicatePackage, "该精听包已经导入");
                if (expectedExistingFingerprint is not null)
                {
                    var installed = Path.Combine(destination, "manifest.json");
                    if (!File.Exists(installed) || IlpManifest.FromBytes(await File.ReadAllBytesAsync(installed, ct)).ContentFingerprint() != expectedExistingFingerprint)
                        throw new IOException("本地课程已更改，请重试并核对要替换的版本。");
                }
                ct.ThrowIfCancellationRequested();
                if (replacing) Directory.Move(destination, backup);
                try { Directory.Move(staging, destination); }
                catch
                {
                    if (replacing) Directory.Move(backup, destination);
                    throw;
                }
            }
            finally { InstallGate.Release(); }
            if (Directory.Exists(backup))
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

    private static async Task ExtractPayloadAsync(Dictionary<string, ZipArchiveEntry> entries, IlpManifest manifest, string staging, CancellationToken ct)
    {
        var audio = Required(entries, manifest.AudioPath);
        var transcript = Required(entries, manifest.TranscriptPath);
        // ReadManifestAsync has already consumed this entry through its bounded stream.
        var remaining = MaximumPackageBytes - entries["manifest.json"].Length;
        var buffer = new byte[65536];
        foreach (var entry in entries.Values)
        {
            if (entry.FullName == "manifest.json") continue;
            var expectedHash = entry == audio ? manifest.AudioSha256 : entry == transcript ? manifest.TranscriptSha256 : null;
            var limit = entry == transcript ? Math.Min(remaining, MaximumTranscriptBytes) : remaining;
            await using var input = ArchiveSafety.OpenRead(entry, limit);
            if (expectedHash is null)
            {
                await input.CopyToAsync(Stream.Null, ct);
            }
            else
            {
                await using var output = new FileStream(Path.Combine(staging, entry.FullName), FileMode.CreateNew);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedHash)
                    throw new IlpException(IlpError.HashMismatch, $"文件 SHA-256 校验失败：{entry.FullName}");
            }
            // OpenRead verifies the actual byte count equals entry.Length at EOF.
            remaining -= entry.Length;
        }
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
        if (archive.Entries.Count > 2000) throw new InvalidDataException("压缩包文件数量过多");
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
