using System.Text.Json;
using IL.Core.Settings;
using IL.Core.Telemetry;
using Xunit;

namespace IL.Core.Tests;

public sealed class PerformanceAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il-performance-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact]
    public async Task ReportsFiveHundredSamplesAndDropsTimersWhenDisabled()
    {
        var transport = new Transport();
        var telemetry = new AppTelemetry(transport: transport, supportedPlatform: true, dataDirectory: _root);
        await telemetry.ApplyConsentAsync(true, true);
        telemetry.ConfigurePerformanceAnalysis(true);
        for (var i = 0; i < 499; i++) telemetry.StartPerformanceTimer("player.test")!.Dispose();
        Assert.False(transport.Report.Task.IsCompleted);
        telemetry.StartPerformanceTimer("player.test")!.Dispose();
        var fields = await transport.Report.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var samples = JsonDocument.Parse(fields["samples"]);
        Assert.Equal(500, samples.RootElement.GetArrayLength());
        Assert.All(samples.RootElement.EnumerateArray(), sample =>
        {
            Assert.Equal("player.test", sample.GetProperty("operation").GetString());
            Assert.True(sample.GetProperty("durationMs").GetDouble() >= 0);
            Assert.Equal(2, sample.EnumerateObject().Count());
        });
        using var abandoned = telemetry.StartPerformanceTimer("player.abandoned");
        telemetry.ConfigurePerformanceAnalysis(false);
        Assert.Null(telemetry.StartPerformanceTimer("player.disabled"));
        await telemetry.ShutdownAsync();
    }

    [Fact]
    public async Task SlowOperationReportsImmediatelyAndConsentControlsCollection()
    {
        var transport = new Transport();
        var telemetry = new AppTelemetry(transport: transport, supportedPlatform: true, dataDirectory: _root);
        telemetry.ConfigurePerformanceAnalysis(true);
        Assert.Null(telemetry.StartPerformanceTimer("before.consent"));
        await telemetry.ApplyConsentAsync(true, true);
        var timer = telemetry.StartPerformanceTimer("library.load")!;
        await Task.Delay(2050); timer.Dispose(); timer.Dispose();
        var fields = await transport.Report.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var samples = JsonDocument.Parse(fields["samples"]);
        Assert.Single(samples.RootElement.EnumerateArray());
        Assert.True(samples.RootElement[0].GetProperty("durationMs").GetDouble() > 2000);
        await telemetry.ApplyConsentAsync(false, true);
        Assert.Null(telemetry.StartPerformanceTimer("after.withdrawal"));
    }

    [Fact]
    public void PerformancePreferencesRoundTrip()
    {
        var settings = AppSettings.Defaults() with { PerformanceMode = true, DisableAnimationWaiting = false, PerformanceAnalysisEnabled = false };
        var restored = AppSettings.FromJson(settings.ToJson());
        Assert.True(restored.PerformanceMode); Assert.False(restored.DisableAnimationWaiting); Assert.False(restored.PerformanceAnalysisEnabled);
    }

    private sealed class Transport : ITelemetryTransport
    {
        public TaskCompletionSource<IReadOnlyDictionary<string, string>> Report { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> StartAsync(string version, string cachePath) => Task.FromResult(true);
        public Task StopAsync(string cachePath) => Task.CompletedTask;
        public Task<bool> EventAsync(string name, IReadOnlyDictionary<string, string> fields)
        { if (name == "track_timer") Report.TrySetResult(fields); return Task.FromResult(true); }
        public Task<bool> ErrorLogAsync(string text) => Task.FromResult(true);
        public Task<string?> InstallCycleAsync() => Task.FromResult<string?>(null);
        public Task<string?> InstallUuidAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<string, string>> SystemProfileAsync() => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<bool> CanReachCollectorAsync() => Task.FromResult(false);
    }
}
