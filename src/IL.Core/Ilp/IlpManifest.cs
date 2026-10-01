using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IL.Core.Models;

namespace IL.Core.Ilp;

public sealed partial record IlpManifest(int FormatVersion, string Title, TimeSpan Duration, string AudioPath,
    string TranscriptPath, string AudioSha256, string TranscriptSha256, string PackageUuid, int PackageVersion, LessonExercises Exercises)
{
    public const int SupportedVersion = 2;
    public const string CanonicalTranscriptPath = "transcript.srt";
    public static IReadOnlySet<string> SupportedAudioExtensions { get; } = new HashSet<string> { "m4a", "mp3", "wav" };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex UuidPattern();
    [GeneratedRegex(@"^[a-f0-9]{64}$")] private static partial Regex HashPattern();
    [GeneratedRegex(@"^audio\.([A-Za-z0-9]+)$")] private static partial Regex AudioPattern();

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        FormatVersion, Title, durationMs = (long)Duration.TotalMilliseconds, AudioPath, TranscriptPath,
        PackageUuid, PackageVersion, Exercises,
        sha256 = new Dictionary<string, string> { [AudioPath] = AudioSha256, [TranscriptPath] = TranscriptSha256 }
    }, JsonOptions);

    public static IlpManifest FromBytes(byte[] bytes)
    {
        if (bytes.Length > 256 * 1024) throw new IlpException(IlpError.InvalidManifest, "manifest.json 过大");
        try
        {
            using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("manifest.json 顶层必须是对象");
            if (!root.TryGetProperty("formatVersion", out var v) || !v.TryGetInt32(out var version)) throw Invalid("formatVersion 必须是整数");
            if (version != SupportedVersion) throw new IlpException(IlpError.UnsupportedVersion, $"不支持的 .ilp 格式版本：{version}");
            var title = String(root, "title");
            if (string.IsNullOrWhiteSpace(title) || title.Length > 300) throw Invalid("title 必须是非空字符串");
            if (!root.TryGetProperty("durationMs", out var d) || !d.TryGetInt64(out var milliseconds) || milliseconds <= 0 || milliseconds > TimeSpan.MaxValue.TotalMilliseconds)
                throw Invalid("durationMs 必须是正整数");
            var audio = String(root, "audioPath");
            var transcript = String(root, "transcriptPath");
            var match = AudioPattern().Match(audio);
            if (!match.Success || !SupportedAudioExtensions.Contains(match.Groups[1].Value.ToLowerInvariant()) || transcript != CanonicalTranscriptPath)
                throw new IlpException(IlpError.InvalidPath, "精听包仅支持标准音频文件与 transcript.srt");
            var uuid = String(root, "packageUuid");
            if (!UuidPattern().IsMatch(uuid)) throw Invalid("packageUuid 必须是 UUID");
            if (!root.TryGetProperty("packageVersion", out var pv) || !pv.TryGetInt32(out var packageVersion) || packageVersion <= 0)
                throw Invalid("packageVersion 必须是正整数");
            if (!root.TryGetProperty("sha256", out var hashes) || hashes.ValueKind != JsonValueKind.Object) throw Invalid("sha256 必须是对象");
            var audioHash = String(hashes, audio);
            var transcriptHash = String(hashes, transcript);
            if (!HashPattern().IsMatch(audioHash) || !HashPattern().IsMatch(transcriptHash)) throw Invalid("SHA-256 必须是 64 位小写十六进制字符串");
            var exercises = root.TryGetProperty("exercises", out var e) ? LessonExercises.FromJson(e) : LessonExercises.Empty;
            return new(version, title.Trim(), TimeSpan.FromMilliseconds(milliseconds), audio, transcript, audioHash, transcriptHash, uuid, packageVersion, exercises);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or OverflowException)
        { throw new IlpException(IlpError.InvalidManifest, "manifest.json 不是有效的 UTF-8 JSON 或字段类型无效", ex); }
    }
    private static string String(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static IlpException Invalid(string message) => new(IlpError.InvalidManifest, message);
}
