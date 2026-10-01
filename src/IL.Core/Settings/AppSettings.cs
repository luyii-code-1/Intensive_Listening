using System.Text.Json;
using System.Text.Json.Nodes;
using IL.Core.Infrastructure;

namespace IL.Core.Settings;

public enum AsrProviderKind { Cloud, Local }
public sealed record AppSettings
{
    public AsrProviderKind AsrProvider { get; init; } = AsrProviderKind.Cloud;
    public string CloudBaseUrl { get; init; } = "https://dashscope.aliyuncs.com";
    public string CloudEndpoint { get; init; } = "/api/v1/services/aigc/multimodal-generation/generation";
    public string CloudModel { get; init; } = "qwen-audio-3.0-asr-flash";
    public string CloudApiKey { get; init; } = "";
    public int CloudTimeoutSeconds { get; init; } = 180;
    public int CloudConcurrency { get; init; } = 10;
    public string CloudLanguage { get; init; } = "en";
    public bool TranslateChineseToEnglish { get; init; } = true;
    public bool FileAssociationEnabled { get; init; }
    public bool FileAssociationPrompted { get; init; }
    public bool McpEnabled { get; init; }
    public string LocalModelsDirectory { get; init; } = "";
    public string SelectedLocalModel { get; init; } = "";
    public IReadOnlyList<string> DetectedLocalModels { get; init; } = [];
    public string ThemeMode { get; init; } = "system";
    public bool SkipOpeningPrompts { get; init; } = true;
    public int TranscriptFontSize { get; init; } = 18;
    public bool DebugLogging { get; init; }
    public bool TelemetryEnabled { get; init; }
    public bool TelemetryPrompted { get; init; }
    public string EulaAcceptedVersion { get; init; } = "";
    public string CompletedInstallationId { get; init; } = "";
    public IL.Core.Asr.AsrConfig CloudAsrConfig => new(CloudBaseUrl,CloudEndpoint,CloudModel,CloudApiKey,CloudLanguage);
    public bool CloudReady => new[] { CloudBaseUrl, CloudEndpoint, CloudModel, CloudApiKey }.All(x => !string.IsNullOrWhiteSpace(x));
    public bool LocalReady => !string.IsNullOrWhiteSpace(LocalModelsDirectory) && !string.IsNullOrWhiteSpace(SelectedLocalModel);
    public string AsrCacheProfile => JsonSerializer.Serialize(new { provider = AsrProvider.ToString().ToLowerInvariant(), baseUrl = CloudBaseUrl.Trim(), endpoint = CloudEndpoint.Trim(), model = CloudModel.Trim(), language = CloudLanguage.Trim(), translateChineseToEnglish = TranslateChineseToEnglish, localModel = SelectedLocalModel.Trim(), segmentMaxSeconds = 120 }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public static AppSettings Defaults() => new() { CloudBaseUrl = Environment.GetEnvironmentVariable("ILP_ASR_BASE_URL") ?? "https://dashscope.aliyuncs.com", CloudEndpoint = Environment.GetEnvironmentVariable("ILP_ASR_ENDPOINT") ?? "/api/v1/services/aigc/multimodal-generation/generation", CloudModel = Environment.GetEnvironmentVariable("ILP_ASR_MODEL") ?? "qwen-audio-3.0-asr-flash", CloudApiKey = Environment.GetEnvironmentVariable("ILP_ASR_API_KEY") ?? "" };
    public JsonObject ToJson() { var json = JsonSerializer.SerializeToNode(this, JsonOptions)!.AsObject(); foreach (var key in new[] { "cloudReady", "localReady", "asrCacheProfile", "cloudAsrConfig" }) json.Remove(key); return json; }
    public bool NeedsOnboarding(string installationId) => EulaAcceptedVersion.Length == 0 || (!string.IsNullOrWhiteSpace(installationId) && CompletedInstallationId != installationId);
    public static AppSettings FromJson(JsonObject j) => new()
    {
        CompletedInstallationId = String(j, "completedInstallationId", ""),
        // The released loader always normalizes recognition to the cloud English profile.
        CloudBaseUrl = String(j, "cloudBaseUrl", "https://dashscope.aliyuncs.com"), CloudEndpoint = String(j, "cloudEndpoint", "/api/v1/services/aigc/multimodal-generation/generation"), CloudModel = String(j, "cloudModel", "qwen-audio-3.0-asr-flash"), CloudApiKey = String(j, "cloudApiKey", ""),
        CloudTimeoutSeconds = Integer(j, "cloudTimeoutSeconds", 180), CloudConcurrency = Math.Clamp(Integer(j, "cloudConcurrency", 10), 1, 10), TranslateChineseToEnglish = Bool(j, "translateChineseToEnglish", true), FileAssociationEnabled = Bool(j, "fileAssociationEnabled"), FileAssociationPrompted = Bool(j, "fileAssociationPrompted"), McpEnabled = Bool(j, "mcpEnabled"), LocalModelsDirectory = String(j, "localModelsDirectory", ""), SelectedLocalModel = String(j, "selectedLocalModel", ""), DetectedLocalModels = Strings(j["detectedLocalModels"]), ThemeMode = String(j, "themeMode", "system"), SkipOpeningPrompts = Bool(j, "skipOpeningPrompts", true), TranscriptFontSize = Math.Clamp(Integer(j, "transcriptFontSize", 18), 14, 28), DebugLogging = Bool(j, "debugLogging"), TelemetryEnabled = Bool(j, "telemetryEnabled"), TelemetryPrompted = Bool(j, "telemetryPrompted"), EulaAcceptedVersion = String(j, "eulaAcceptedVersion", "")
    };
    internal static string String(JsonObject j, string k, string d) => j[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : d;
    internal static bool Bool(JsonObject j, string k, bool d = false) => j[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
    internal static int Integer(JsonObject j, string k, int d) { var n = j[k]; if (n == null) return d; if (int.TryParse(n.ToString(), out var i)) return i; return double.TryParse(n.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f) ? (int)Math.Round(f, MidpointRounding.AwayFromZero) : d; }
    internal static string[] Strings(JsonNode? n) => n is JsonArray a ? a.OfType<JsonValue>().Where(x => x.TryGetValue<string>(out _)).Select(x => x.GetValue<string>()).ToArray() : [];
    public static IReadOnlyList<string> ScanLocalModels(string directory) => !Directory.Exists(directory.Trim()) ? [] : Directory.EnumerateFileSystemEntries(directory.Trim()).Where(x => (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0).Where(x => Directory.Exists(x) || new[] { ".bin", ".gguf", ".onnx", ".xml" }.Contains(Path.GetExtension(x).ToLowerInvariant())).Select(x => Directory.Exists(x) ? Path.GetFileName(x) : Path.GetFileNameWithoutExtension(x)).Distinct().Order(StringComparer.Ordinal).ToArray();
}

public sealed class AppSettingsStore(string? settingsPath = null)
{
    public string FilePath => settingsPath ?? Path.Combine(AppDirectories.DataDirectory(), "settings.json");
    public async Task<AppSettings> LoadAsync() { try { return File.Exists(FilePath) && JsonNode.Parse(await File.ReadAllTextAsync(FilePath)) is JsonObject j ? AppSettings.FromJson(j) : AppSettings.Defaults(); } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return AppSettings.Defaults(); } }
    public async Task SaveAsync(AppSettings settings) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!); await File.WriteAllTextAsync(FilePath, settings.ToJson().ToJsonString(AppSettings.JsonOptions)); }
}
