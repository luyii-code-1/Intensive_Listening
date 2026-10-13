using IL.Core.Settings;
using System.Text.Json.Nodes;
using Xunit;

namespace IL.Core.Tests;

public sealed class InstallationOnboardingTests
{
    [Fact]
    public void UpgradeShowsWelcomeWhileNewOrResetDataKeepsFullSetupUntilCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-onboarding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var marker = Path.Combine(root, "installation-onboarding.txt");
            File.WriteAllText(marker, "welcome");
            InstallationOnboarding.Prepare(root, hasPersonalSettings: true); Assert.True(InstallationOnboarding.WelcomeOnly(root));
            InstallationOnboarding.Prepare(root, hasPersonalSettings: false); Assert.False(InstallationOnboarding.WelcomeOnly(root));
            InstallationOnboarding.Prepare(root, hasPersonalSettings: true); Assert.False(InstallationOnboarding.WelcomeOnly(root));
            File.WriteAllText(marker, "full"); Assert.False(InstallationOnboarding.WelcomeOnly(root));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void DefaultPlaybackBindingPreservesUserKeysAndDoesNotTakeOccupiedPageUp()
    {
        Assert.Equal("PageUp", AppSettings.Defaults().PlayerKeyBindings[PlayerShortcutAction.TogglePlayback]);
        var custom = AppSettings.FromJson(new JsonObject { ["playerKeyBindings"] = new JsonObject { ["togglePlayback"] = "PageDown" } });
        Assert.Equal("PageDown", custom.PlayerKeyBindings[PlayerShortcutAction.TogglePlayback]);
        var occupied = AppSettings.FromJson(new JsonObject { ["playerKeyBindings"] = new JsonObject { ["nextCue"] = "PageUp" } });
        Assert.Equal("PageUp", occupied.PlayerKeyBindings[PlayerShortcutAction.NextCue]);
        Assert.False(occupied.PlayerKeyBindings.ContainsKey(PlayerShortcutAction.TogglePlayback));
    }
    [Fact]
    public async Task CompletedSetupSurvivesRestartAndReopensForNextInstallation()
    {
        var root = Path.Combine(Path.GetTempPath(), "il2-installation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(Path.Combine(root, "settings.json"));
            var previous = new AppSettings { EulaAcceptedVersion = "2026-09-22", CloudApiKey = "local-test-key", ThemeMode = "dark", TelemetryEnabled = false };
            Assert.True(previous.NeedsOnboarding("installation-one"));
            await store.SaveAsync(previous with { CompletedInstallationId = "installation-one" });
            var restarted = await new AppSettingsStore(store.FilePath).LoadAsync();
            Assert.False(restarted.NeedsOnboarding("installation-one"));
            Assert.True(restarted.NeedsOnboarding("installation-two"));
            Assert.Equal(previous.CloudApiKey, restarted.CloudApiKey);
            Assert.Equal(previous.ThemeMode, restarted.ThemeMode);
            Assert.False(restarted.TelemetryEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
