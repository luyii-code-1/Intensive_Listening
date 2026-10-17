using System.Text.Json;
using System.Text.Json.Nodes;
using IL.Core.Settings;
namespace IL.Core.Management;

public sealed record ManagedUpdate(string Id, string Mode, string Version, string Url, string Sha256, long Size, string Signature)
{
    public string SignedContent => string.Join('\n', Id, Mode, Version, Url, Sha256, Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
public sealed record ManagedPolicy
{
    public long Revision { get; init; }
    public JsonObject Settings { get; init; } = new();
    public string[] LockedSettings { get; init; } = [];
    public string[] DisabledFeatures { get; init; } = [];
    public bool LockAllSettings { get; init; }
    public bool LockManagement { get; init; }
    public bool HideAsrConfiguration { get; init; }
    public bool AutomaticOobe { get; init; }
    public ManagedUpdate? Update { get; init; }
    public ManagedLesson[] Lessons { get; init; } = [];
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static readonly string[] Features = ["playback", "importAudio", "importIlp", "authoring", "asr", "mcp", "export", "importProject", "dataMaintenance", "apiConfiguration", "updates"];
    // Legal consent and optional Aliyun consent remain personal; MDN telemetry has a separate transport.
    private static readonly string[] LocalOnly = ["eulaAcceptedVersion", "completedInstallationId", "telemetryEnabled", "telemetryPrompted", "fileAssociationPrompted", "startAtLogin"];
    public static string[] SettingNames => AppSettings.Defaults().ToJson().Select(p => p.Key).Except(LocalOnly).ToArray();
    public bool IsLocked(string name) => !LocalOnly.Contains(name) && (LockAllSettings || LockedSettings.Contains(name) || Settings.ContainsKey(name) || HideAsrConfiguration && IsAsr(name));
    public string[] AllowedImportExtensions() =>
        (DisabledFeatures.Contains("importIlp") ? Array.Empty<string>() : new[] { "ilp" })
        .Concat(DisabledFeatures.Contains("importAudio") ? Array.Empty<string>() : new[] { "mp3", "m4a", "wav", "flac", "ogg" }).ToArray();
    public static bool IsAsr(string name) => name.StartsWith("cloud", StringComparison.Ordinal) || name.StartsWith("local", StringComparison.Ordinal) || name is "asrProvider" or "selectedLocalModel" or "detectedLocalModels" or "translateChineseToEnglish";
    public void Validate()
    {
        var defaults = AppSettings.Defaults().ToJson(); var allowed = SettingNames;
        if (Revision < 0 || Lessons is null || Lessons.Length > 1000 || Lessons.Any(l => l is null || !Guid.TryParse(l.Id, out _) || !Guid.TryParse(l.PackageUuid, out _) || l.PackageVersion < 1) || LockedSettings.Any(s => !allowed.Contains(s)) || DisabledFeatures.Any(s => !Features.Contains(s))) throw new FormatException("集控策略包含未知设置或功能。");
        foreach (var pair in Settings)
            if (!allowed.Contains(pair.Key) || pair.Value is null || JsonType(defaults[pair.Key]) != JsonType(pair.Value)) throw new FormatException("集控策略字段类型无效：" + pair.Key);
        if (HideAsrConfiguration && !Settings.ContainsKey("cloudApiKey")) throw new FormatException("隐藏 API 配置需要下发 API Key。");
        _ = Apply(AppSettings.Defaults());
    }
    private static JsonValueKind? JsonType(JsonNode? value) => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? JsonValueKind.True : value?.GetValueKind();
    public AppSettings Apply(AppSettings personal)
    {
        var json = personal.ToJson(); foreach (var pair in Settings) json[pair.Key] = pair.Value?.DeepClone();
        if (DisabledFeatures.Contains("mcp")) json["mcpEnabled"] = false;
        return AppSettings.FromJson(json);
    }
    public AppSettings PreservePersonal(AppSettings proposed, AppSettings personal)
    {
        var json = proposed.ToJson(); var original = personal.ToJson();
        foreach (var key in SettingNames.Where(IsLocked)) json[key] = original[key]?.DeepClone();
        return AppSettings.FromJson(json);
    }
}
public static class ManagedRuntime
{
    public static MdnClient? Client { get; set; }
    public static ManagedPolicy Policy => Client?.Policy ?? new();
    public static bool IsManaged => Client?.Configuration is not null;
    public static AppSettings Effective(AppSettings settings) => IsManaged ? Policy.Apply(settings) : settings;
    public static AppSettings Display(AppSettings settings) => IsManaged && Policy.HideAsrConfiguration ? settings with
    { CloudApiKey = "", CloudBaseUrl = "", CloudEndpoint = "", CloudModel = "", LocalModelsDirectory = "", SelectedLocalModel = "", DetectedLocalModels = [] } : settings;
    public static bool Allows(string feature) => !IsManaged || !Policy.DisabledFeatures.Contains(feature);
    public static string Redact(string text)
    {
        if(!IsManaged)return text;
        foreach(var field in Policy.Settings.Where(p=>p.Key=="cloudApiKey"||Policy.HideAsrConfiguration&&ManagedPolicy.IsAsr(p.Key)))
            if(field.Value is JsonValue value && value.TryGetValue<string>(out var secret)&&secret.Length>2)text=text.Replace(secret,"[managed]",StringComparison.Ordinal);
        if(Client?.Configuration is {} config)text=text.Replace(config.EnrollmentToken,"[managed]",StringComparison.Ordinal);
        return text;
    }
    public static void Require(string feature) { if (!Allows(feature)) throw new ManagedFeatureDeniedException(feature); }
}

public sealed class ManagedFeatureDeniedException(string feature) : InvalidOperationException("该功能由您的组织管理。")
{
    public string Feature { get; } = feature;
}
