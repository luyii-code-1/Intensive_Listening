using System.Security.Cryptography;
using System.Text;
using IL.Core.Management;
using IL.Core.Settings;
using Xunit;

namespace IL.Core.Tests;

public sealed class ManagedRoleTests
{
    [Fact]
    public void StudentLocksAuthoringAndPreservesPersonalPreferences()
    {
        var personal = AppSettings.Defaults() with { McpEnabled = true, CreateProjectOnAudioImport = true, CloudApiKey = "personal", CloudServiceEnabled = true };
        var policy = new ManagedPolicy { ClientRole = "student", AsrMode = "forceCloud" };
        policy.Validate(); var effective = policy.Apply(personal);
        Assert.False(policy.Allows("authoring")); Assert.False(policy.Allows("asr")); Assert.False(policy.Allows("mcp"));
        Assert.True(policy.Allows("playback")); Assert.True(policy.Allows("importIlp"));
        Assert.False(effective.McpEnabled); Assert.False(effective.CreateProjectOnAudioImport);
        Assert.True(effective.CloudServiceEnabled); Assert.False(policy.IsLocked("cloudServiceEnabled"));
        Assert.True(policy.IsLocked("cloudApiKey")); Assert.True(policy.IsLocked("asrServiceMode"));
        var saved = policy.PreservePersonal(effective with { CloudApiKey = "changed" }, personal);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(personal.ToJson(), saved.ToJson()));
    }
    [Theory]
    [InlineData("followLocal", AsrServiceMode.Api, false)]
    [InlineData("forceApi", AsrServiceMode.Api, true)]
    [InlineData("forceCloud", AsrServiceMode.CloudService, true)]
    public void ModeOverrideIsReversible(string mode, AsrServiceMode expected, bool locked)
    {
        var personal = AppSettings.Defaults(); var policy = new ManagedPolicy { AsrMode = mode };
        policy.Validate(); var effective = policy.Apply(personal);
        Assert.Equal(expected, effective.AsrServiceMode); Assert.Equal(locked, policy.IsLocked("asrServiceMode"));
        Assert.Equal(AsrServiceMode.Api, policy.PreservePersonal(effective, personal).AsrServiceMode);
    }
    [Fact]
    public void NewCloudPreferencesRoundTripWithConnectionOffByDefault()
    {
        Assert.False(AppSettings.Defaults().CloudServiceEnabled);
        var personal = AppSettings.Defaults() with { CloudServiceEnabled = true, AsrServiceMode = AsrServiceMode.CloudService };
        var restored = AppSettings.FromJson(personal.ToJson());
        Assert.True(restored.CloudServiceEnabled); Assert.Equal(AsrServiceMode.CloudService, restored.AsrServiceMode);
        Assert.DoesNotContain("cloudApiKey", restored.AsrCacheProfile);
    }
    [Fact]
    public void ManagedCacheRejectsTamperingAndDifferentPurpose()
    {
        var key = RandomNumberGenerator.GetBytes(32); var original = Encoding.UTF8.GetBytes("private device credential and policy");
        var envelope = ManagedSecretStore.Seal(original, key, "state.bin");
        Assert.Equal(original, ManagedSecretStore.Open(envelope, key, "state.bin"));
        Assert.ThrowsAny<CryptographicException>(() => ManagedSecretStore.Open(envelope, key, "events.bin"));
        envelope[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => ManagedSecretStore.Open(envelope, key, "state.bin"));
    }
}
