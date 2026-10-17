using System.Text.Json;
using System.Text.Json.Nodes;
using IL.Core.Management;
using IL.Core.Settings;
using Xunit;

namespace IL.Core.Tests;

public sealed class ManagedSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupPreferenceRemainsPersonalAcrossManagedSettings(bool preference)
    {
        var personal = AppSettings.Defaults() with { StartAtLogin = preference };
        var restored = AppSettings.FromJson(personal.ToJson());
        Assert.Equal(preference, restored.StartAtLogin);
        Assert.DoesNotContain("startAtLogin", ManagedPolicy.SettingNames);
        var effective = new ManagedPolicy { LockAllSettings = true }.Apply(restored);
        Assert.Equal(preference, effective.StartAtLogin);
    }
    [Theory]
    [InlineData(false, false, 6)]
    [InlineData(true, false, 5)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 0)]
    public void ImportPickerHonorsBothIndependentFeatureRestrictions(bool ilpDisabled, bool audioDisabled, int count)
    {
        var features = new List<string>();
        if (ilpDisabled) features.Add("importIlp"); if (audioDisabled) features.Add("importAudio");
        var policy = new ManagedPolicy { DisabledFeatures = features.ToArray() };
        policy.Validate(); var extensions = policy.AllowedImportExtensions();
        Assert.Equal(count, extensions.Length);
        Assert.Equal(!ilpDisabled, extensions.Contains("ilp"));
        Assert.Equal(!audioDisabled, extensions.Contains("mp3"));
        Assert.DoesNotContain("playback", policy.DisabledFeatures);
    }
    [Fact]
    public void ServerAndClientAgreeOnEveryManagedSettingAndType()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "mdn-settings-schema.json")))!.AsObject();
        Assert.Equal(schema.Select(p => p.Key).Order(), ManagedPolicy.SettingNames.Order());
        var defaults = AppSettings.Defaults().ToJson();
        foreach (var (name, declared) in schema)
        {
            var kind = defaults[name]!.GetValueKind();
            var actual = kind switch { JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Number => "number", JsonValueKind.String => "string", JsonValueKind.Array => "array", JsonValueKind.Object => "object", _ => "invalid" };
            Assert.Equal(declared!.GetValue<string>(), actual);
            new ManagedPolicy { Settings = new JsonObject { [name] = defaults[name]!.DeepClone() } }.Validate();
        }
    }

    [Fact]
    public void EveryBooleanCanBeEnabledAndDisabledInDownloadedPolicy()
    {
        var defaults = AppSettings.Defaults().ToJson();
        foreach (var name in ManagedPolicy.SettingNames.Where(n => defaults[n]!.GetValueKind() is JsonValueKind.True or JsonValueKind.False))
        foreach (var value in new[] { true, false })
        {
            var wire = JsonSerializer.Serialize(new { settings = new Dictionary<string, bool> { [name] = value } });
            var policy = JsonSerializer.Deserialize<ManagedPolicy>(wire, ManagedPolicy.Json)!;
            policy.Validate();
            Assert.Equal(value, policy.Apply(AppSettings.Defaults()).ToJson()[name]!.GetValue<bool>());
            Assert.True(policy.IsLocked(name));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FileAssociationPolicyOverridesLocalChoiceWithoutChangingPromptHistory(bool enabled)
    {
        var personal = AppSettings.Defaults() with { FileAssociationEnabled = !enabled, FileAssociationPrompted = false };
        var policy = new ManagedPolicy { Settings = new JsonObject { ["fileAssociationEnabled"] = enabled } };
        policy.Validate(); var effective = policy.Apply(personal);
        Assert.Equal(enabled, effective.FileAssociationEnabled);
        Assert.False(effective.FileAssociationPrompted);
        Assert.Equal(!enabled, policy.PreservePersonal(effective, personal).FileAssociationEnabled);
        Assert.Throws<FormatException>(() => new ManagedPolicy { Settings = new JsonObject { ["fileAssociationEnabled"] = "true" } }.Validate());
    }
}
