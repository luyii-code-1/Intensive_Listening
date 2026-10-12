using System.Text.Json.Nodes;
using IL.Core.Management;
using IL.Core.Settings;
using Xunit;

namespace IL.Core.Tests;

public sealed class DeploymentPresetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il-preset-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task PresetOverwritesSpecifiedFieldsOnceAndPreservesSecretsAndConsent()
    {
        Directory.CreateDirectory(_root);
        var store = new AppSettingsStore(Path.Combine(_root, "settings.json"));
        await store.SaveAsync(AppSettings.Defaults() with { CloudApiKey = "personal-secret", EulaAcceptedVersion = "accepted", TelemetryEnabled = false, ThemeMode = "light", McpEnabled = true });
        var yaml = Path.Combine(_root, "il_precfg.yaml");
        await File.WriteAllTextAsync(yaml, "schema_version: 1\nsettings:\n  themeMode: dark\n  mcpEnabled: false\n  transcriptFontSize: 24\n");
        Assert.True(await DeploymentPreset.ApplyAsync(store));
        var settings = await store.LoadAsync();
        Assert.Equal("dark", settings.ThemeMode); Assert.False(settings.McpEnabled); Assert.Equal(24, settings.TranscriptFontSize);
        Assert.Equal("personal-secret", settings.CloudApiKey); Assert.Equal("accepted", settings.EulaAcceptedVersion); Assert.False(settings.TelemetryEnabled);
        await store.SaveAsync(settings with { ThemeMode = "light" });
        Assert.False(await DeploymentPreset.ApplyAsync(store)); Assert.Equal("light", (await store.LoadAsync()).ThemeMode);
        await File.WriteAllTextAsync(yaml, "schema_version: 1\nsettings:\n  themeMode: dark\n");
        Assert.True(await DeploymentPreset.ApplyAsync(store)); Assert.Equal("dark", (await store.LoadAsync()).ThemeMode);
        var policy = new ManagedPolicy { Settings = new JsonObject { ["themeMode"] = "light" } };
        Assert.Equal("light", policy.Apply(await store.LoadAsync()).ThemeMode);
        Assert.Equal("dark", (await store.LoadAsync()).ThemeMode);
    }

    [Theory]
    [InlineData("cloudApiKey: secret")]
    [InlineData("telemetryEnabled: true")]
    [InlineData("eulaAcceptedVersion: agreed")]
    [InlineData("enrollmentToken: secret")]
    [InlineData("unknown: true")]
    [InlineData("mcpEnabled: text")]
    [InlineData("themeMode: [dark]")]
    public async Task InvalidOrProtectedFieldsLeaveExistingPreferencesUntouched(string setting)
    {
        Directory.CreateDirectory(_root);
        var store = new AppSettingsStore(Path.Combine(_root, "settings.json"));
        await store.SaveAsync(AppSettings.Defaults() with { ThemeMode = "light" });
        var original = await File.ReadAllTextAsync(store.FilePath);
        await File.WriteAllTextAsync(Path.Combine(_root, "il_precfg.yaml"), "schema_version: 1\nsettings:\n  transcriptFontSize: 24\n  " + setting + "\n");
        await Assert.ThrowsAsync<FormatException>(() => DeploymentPreset.ApplyAsync(store));
        Assert.Equal(original, await File.ReadAllTextAsync(store.FilePath));
    }

    [Fact]
    public void TypedYamlAndShortcutBindingsAreReadCorrectly()
    {
        var values = DeploymentPreset.Parse("schema_version: 1\nsettings:\n  mcpEnabled: false\n  playerKeyBindings:\n    togglePlayback: PageDown\n");
        var settings = new ManagedPolicy { Settings = values }.Apply(AppSettings.Defaults());
        Assert.False(settings.McpEnabled); Assert.Equal("PageDown", settings.PlayerKeyBindings[PlayerShortcutAction.TogglePlayback]);
        Assert.ThrowsAny<Exception>(() => DeploymentPreset.Parse("schema_version: 1\nsettings:\n  themeMode: dark\n  themeMode: light\n"));
        Assert.Throws<FormatException>(() => DeploymentPreset.Parse("schema_version: 2\nsettings: {}\n"));
    }
}
