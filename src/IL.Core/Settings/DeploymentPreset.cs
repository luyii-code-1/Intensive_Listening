using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IL.Core.Management;
using YamlDotNet.Serialization;

namespace IL.Core.Settings;

public static class DeploymentPreset
{
    public static JsonObject Read(string source)
    {
        if (new FileInfo(source).Length > 32 * 1024) throw new FormatException("预设配置文件不能超过 32 KiB。");
        return Parse(File.ReadAllText(source));
    }
    public static JsonObject Parse(string yaml)
    {
        var document = new DeserializerBuilder().WithDuplicateKeyChecking().WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object>(yaml);
        var json = JsonNode.Parse(new SerializerBuilder().JsonCompatible().Build().Serialize(document)) as JsonObject;
        if (json is null || json.Any(p => p.Key is not ("schema_version" or "settings")) ||
            json["schema_version"] is not JsonValue schema || !schema.TryGetValue<int>(out var version) || version != 1 || json["settings"] is not JsonObject values)
            throw new FormatException("预设配置需要 schema_version: 1 和 settings 字段。");
        if (values.ContainsKey("cloudApiKey")) throw new FormatException("预设配置不支持 API 密钥。");
        new ManagedPolicy { Settings = values }.Validate();
        return values;
    }
    public static async Task<bool> ApplyAsync(AppSettingsStore store, bool force = false)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(store.FilePath))!;
        var source = Path.Combine(root, "il_precfg.yaml");
        if (!File.Exists(source)) return false;
        var values = Read(source);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(values.ToJsonString())));
        var marker = Path.Combine(root, "installation", "preset-applied.sha256");
        if (!force && File.Exists(marker) && File.ReadAllText(marker).Trim() == hash && store.HasPersonalSettings) return false;
        var personal = await store.LoadPersonalAsync();
        var proposed = new ManagedPolicy { Settings = values }.Apply(personal);
        if (values.ContainsKey("fileAssociationEnabled")) proposed = proposed with { FileAssociationPrompted = true };
        await store.SavePersonalAsync(proposed);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, hash);
        return true;
    }
}
