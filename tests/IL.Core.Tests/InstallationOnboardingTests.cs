using IL.Core.Settings;
using Xunit;

namespace IL.Core.Tests;

public sealed class InstallationOnboardingTests
{
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
