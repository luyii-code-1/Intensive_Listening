using Xunit;
using IL.Core.Infrastructure;
using IL.Core.Telemetry;

namespace IL.Core.Tests;

public sealed class CrashReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il-crash-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact]
    public async Task OfflineReportRetriesOnceAndKeepsLocalReport()
    {
        var store = new CrashReportStore(_root); var report = CrashReport.FromException(new InvalidOperationException("scroll failure"), "审阅", true, true); store.Save(report);
        await store.FlushAsync(true, _ => Task.FromResult(false)); Assert.Null(store.Reports().Single().AcceptedByTelemetryAt);
        var sent = 0; await store.FlushAsync(true, _ => { sent++; return Task.FromResult(true); });
        await store.FlushAsync(true, _ => { sent++; return Task.FromResult(true); });
        Assert.Equal(1, sent); Assert.NotNull(store.Reports().Single().AcceptedByTelemetryAt); Assert.Contains("scroll failure", CrashReportStore.Load(store.PathFor(report.Id))!.Details);
    }
    [Fact]
    public async Task ConsentRevocationPreventsOldAndNewReportsFromUploading()
    {
        var store = new CrashReportStore(_root);
        store.Save(new CrashReport { UploadAllowed = true }); store.Save(new CrashReport { UploadAllowed = false });
        Task<bool> Forbidden(CrashReport _) => throw new InvalidOperationException("Report sent without consent");
        await store.FlushAsync(false, Forbidden); await store.FlushAsync(true, Forbidden);
        Assert.All(store.Reports(), report => { Assert.False(report.UploadAllowed); Assert.Null(report.AcceptedByTelemetryAt); });
    }
    [Fact]
    public async Task ConcurrentReportersSubmitTheSameReportOnlyOnce()
    {
        var store = new CrashReportStore(_root); store.Save(new CrashReport { UploadAllowed = true });
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); var sent = 0;
        var first = store.FlushAsync(true, async _ => { sent++; entered.SetResult(); await release.Task; return true; }); await entered.Task;
        await new CrashReportStore(_root).FlushAsync(true, _ => { sent++; return Task.FromResult(true); }); release.SetResult(); await first;
        Assert.Equal(1, sent);
    }
    [Fact]
    public void ReportsRedactCredentialsAndIgnorePartialFiles()
    {
        var store = new CrashReportStore(_root); store.Save(CrashReport.FromException(new Exception("https://example.com?api_key=private Authorization: Bearer verysecret sk-0123456789ABC"), "ASR", false, true));
        File.WriteAllText(Path.Combine(store.DirectoryPath, "partial.json"), "{"); File.WriteAllText(Path.Combine(store.DirectoryPath, "ignored.tmp"), "{");
        var report = store.Reports().Single(); Assert.DoesNotContain("private", report.Details); Assert.DoesNotContain("verysecret", report.Details); Assert.DoesNotContain("sk-0123", report.Details);
    }
    [Fact]
    public async Task CrashTelemetryRequiresConsentAndConnectivityAndRetainsNativeCacheOnShutdown()
    {
        var transport = new Transport(); var telemetry = new AppTelemetry(transport: transport, dataDirectory: _root, supportedPlatform: true); var report = new CrashReport { UploadAllowed = true };
        Assert.False(await telemetry.ReportCrashAsync(report));
        await telemetry.ApplyConsentAsync(true, true); Assert.False(await telemetry.ReportCrashAsync(report)); Assert.Empty(transport.Errors);
        transport.Online = true; Assert.True(await telemetry.ReportCrashAsync(report));
        var crash = Assert.Single(transport.Events, e => e.Name == "error_track");
        Assert.Equal(report.Id, crash.Fields["report_id"]); Assert.Equal(report.ExceptionType, crash.Fields["exception_type"]);
        Assert.Equal("true", crash.Fields["fatal"]); Assert.Empty(transport.Errors);
        await telemetry.ShutdownAsync(); Assert.Equal("", transport.StoppedPath); Assert.False(await telemetry.ReportCrashAsync(report));
    }
    [Fact]
    public async Task CrashEventRetriesWithSameIdAndRedactsDiagnosticSecrets()
    {
        var transport = new Transport { Online = true, AcceptEvents = false };
        var telemetry = new AppTelemetry(transport: transport, dataDirectory: _root, supportedPlatform: true);
        await telemetry.ApplyConsentAsync(true, true);
        var store = new CrashReportStore(_root);
        var report = new CrashReport { UploadAllowed = true, Message = "Authorization: Bearer private-token", Details = "api_key=private-key", Fatal = false, ExitCode = 12 };
        store.Save(report);
        await store.FlushAsync(true, telemetry.ReportCrashAsync);
        Assert.Null(store.Reports().Single().AcceptedByTelemetryAt);
        transport.AcceptEvents = true;
        await store.FlushAsync(true, telemetry.ReportCrashAsync);
        await store.FlushAsync(true, telemetry.ReportCrashAsync);
        var attempts = transport.Events.Where(e => e.Name == "error_track").ToArray();
        Assert.Equal(2, attempts.Length); Assert.All(attempts, e => Assert.Equal(report.Id, e.Fields["report_id"]));
        Assert.Equal("false", attempts[1].Fields["fatal"]); Assert.Equal("12", attempts[1].Fields["exit_code"]);
        Assert.DoesNotContain("private-token", attempts[1].Fields["message"]); Assert.DoesNotContain("private-key", attempts[1].Fields["details"]);
        Assert.Empty(transport.Errors); Assert.NotNull(store.Reports().Single().AcceptedByTelemetryAt);
        await telemetry.ShutdownAsync();
    }
    private sealed class Transport : ITelemetryTransport
    {
        public bool Online; public bool AcceptEvents = true; public string? StoppedPath; public readonly List<string> Errors = [];
        public readonly List<(string Name, IReadOnlyDictionary<string, string> Fields)> Events = [];
        public Task<bool> StartAsync(string version, string cachePath) => Task.FromResult(true);
        public Task StopAsync(string cachePath) { StoppedPath = cachePath; return Task.CompletedTask; }
        public Task<bool> EventAsync(string name, IReadOnlyDictionary<string, string> fields) { Events.Add((name, fields)); return Task.FromResult(name != "error_track" || AcceptEvents); }
        public Task<bool> ErrorLogAsync(string text) { Errors.Add(text); return Task.FromResult(true); }
        public Task<string?> InstallCycleAsync() => Task.FromResult<string?>(null);
        public Task<string?> InstallUuidAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<string, string>> SystemProfileAsync() => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<bool> CanReachCollectorAsync() => Task.FromResult(Online);
    }
}
