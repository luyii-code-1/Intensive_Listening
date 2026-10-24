using System.Net;
using System.Text.Json;
using IL.Core.Cloud;
using Xunit;

namespace IL.Core.Tests;

public sealed class DesktopCloudAsrTests
{
    [Fact]
    public async Task LostChunkAcknowledgementResumesFromServerOffset()
    {
        using var fixture = new Fixture { LoseChunkAcknowledgement = true };
        Assert.Equal("subtitle", await fixture.Run());
        Assert.Equal(new long[] { 0, 1048576 }, fixture.Offsets);
        Assert.Equal(1, fixture.CompletedUploads);
        Assert.Equal(.5m, fixture.Charged);
    }

    [Fact]
    public async Task LostAcceptanceReusesUploadAndIdempotencyKey()
    {
        using var fixture = new Fixture { LoseAcceptance = true };
        await Assert.ThrowsAsync<CloudAsrWaitingInterruptedException>(() => fixture.Run());
        Assert.Equal("subtitle", await fixture.Run());
        Assert.Equal(1, fixture.CompletedUploads);
        Assert.Equal(2, fixture.SubmissionKeys.Count);
        Assert.Equal(fixture.SubmissionKeys[0], fixture.SubmissionKeys[1]);
    }

    [Fact]
    public async Task ExplicitCancellationUsesFreeServerSettlement()
    {
        using var fixture = new Fixture { UserCancelled = true };
        fixture.Accepted = fixture.Stop.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run());
        Assert.Equal(1, fixture.Cancels);
        Assert.Equal(0m, fixture.Charged);
    }

    [Fact]
    public async Task FailedTaskRetriesThroughExistingTaskInsteadOfReuploading()
    {
        using var fixture = new Fixture { Failed = true };
        await Assert.ThrowsAsync<IL.Core.Asr.AsrException>(() => fixture.Run());
        Assert.Equal("subtitle", await fixture.Run());
        Assert.Equal(1, fixture.Retries); Assert.Equal(1, fixture.CompletedUploads);
        Assert.Single(fixture.SubmissionKeys);
    }
    [Fact]
    public async Task CancellingInterruptedAcceptanceResolvesOriginalTaskBeforeCancellation()
    {
        using var fixture = new Fixture { LoseAcceptance = true };
        await Assert.ThrowsAsync<CloudAsrWaitingInterruptedException>(() => fixture.Run());
        await fixture.Cancel();
        Assert.Equal(1, fixture.CompletedUploads); Assert.Equal(1, fixture.Cancels);
        Assert.Equal(2, fixture.SubmissionKeys.Count); Assert.Equal(fixture.SubmissionKeys[0], fixture.SubmissionKeys[1]);
        Assert.Equal(0m, fixture.Charged);
    }

    [Fact]
    public async Task RestoredQueueWaitsForRemoteCancellationAndDoesNotResumeIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "asr-cancel-queue-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new IL.Core.Transcription.QueueStore(Path.Combine(root, "queue.json"));
            await store.SaveAsync([new("job", "audio", "missing.mp3", IL.Core.Transcription.TranscriptionJobStatus.Interrupted,
                IL.Core.Transcription.TranscriptionStage.Recognizing, "断线", DateTimeOffset.Now, CacheProfile: "desktop-asr-v2")]);
            await using var queue = new IL.Core.Transcription.TranscriptionQueue((_, _, _, _) => throw new InvalidOperationException("Must not restart transcription"),
                _ => Task.FromResult<IL.Core.Transcription.DuplicateMatch?>(null), store, new IL.Core.Transcription.SrtRecognitionCache(Path.Combine(root, "cache")), () => "desktop-asr-v2");
            var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            queue.CancelRemoteJob = _ => { calls++; return confirmed.Task; };
            await queue.RestoreAsync(); queue.Cancel("job", userInitiated: true);
            Assert.Equal(IL.Core.Transcription.TranscriptionJobStatus.Interrupted, queue.JobById("job")!.Status);
            queue.Cancel("job", userInitiated: true); queue.Retry("job"); queue.ResumeInterrupted();
            Assert.Equal(1, calls); Assert.Equal(IL.Core.Transcription.TranscriptionJobStatus.Interrupted, queue.JobById("job")!.Status);
            confirmed.SetResult(); await queue.WaitForIdleAsync();
            Assert.Equal(IL.Core.Transcription.TranscriptionJobStatus.Canceled, queue.JobById("job")!.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "desktop-asr-v2-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient http;
        private readonly CloudAuthClient authentication;
        private readonly DesktopCloudAsrClient client;
        private long uploaded;
        public bool LoseChunkAcknowledgement, LoseAcceptance, UserCancelled, Failed;
        public readonly List<long> Offsets = [];
        public readonly List<string> SubmissionKeys = [];
        public int CompletedUploads, Cancels, Retries;
        public decimal? Charged;
        public Action? Accepted;
        public CancellationTokenSource Stop { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "test.mp3"), new byte[1048576 + 100]);
            http = new(new Handler(Request)); authentication = new(new Tokens(), http); client = new(authentication, root, http);
        }
        public Task<string> Run() => client.RunAsync("job", Path.Combine(root, "test.mp3"), null, Stop.Token, () => UserCancelled, value => Charged = value);
        public Task Cancel() => client.CancelAsync("job", value => Charged = value);
        private async Task<HttpResponseMessage> Request(HttpRequestMessage request)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/connect/token": return Json(new { access_token = "access", refresh_token = "refresh", token_type = "Bearer", expires_in = 900 });
                case "/api/v1/auth/me": return Json(new { id = "user" });
                case "/api/v1/asr/desktop/config": return Json(new { protocol = "desktop-asr-v2", model = "ali", creditsPerMinute = .5m, rounding = "ceil_total_minutes", maxAudioSeconds = 1800, maxAudioBytes = 209715200, chunkBytes = 1048576, paused = false });
                case "/api/v1/asr/desktop/uploads": return Upload();
                case "/api/v1/asr/desktop/uploads/upload":
                    if (request.Method == HttpMethod.Put)
                    {
                        var offset = long.Parse(request.RequestUri.Query.Split('=')[1]); Offsets.Add(offset); Assert.Equal(uploaded, offset);
                        uploaded += (await request.Content!.ReadAsByteArrayAsync()).Length;
                        if (LoseChunkAcknowledgement) { LoseChunkAcknowledgement = false; throw new HttpRequestException("response lost"); }
                    }
                    return Upload();
                case "/api/v1/asr/desktop/uploads/upload/complete": CompletedUploads++; return Json(new { id = "upload", offset = uploaded, bytes = 1048576 + 100, assetId = "asset" });
                case "/api/v1/asr/tasks":
                    Assert.Contains("\"desktopV2\":true", await request.Content!.ReadAsStringAsync());
                    SubmissionKeys.Add(request.Headers.GetValues("Idempotency-Key").Single()); Accepted?.Invoke();
                    if (LoseAcceptance) { LoseAcceptance = false; throw new HttpRequestException("response lost"); }
                    return TaskResponse("queued", 0);
                case "/api/v1/asr/tasks/task": return TaskResponse(Failed ? "failed" : "succeeded", Failed ? 0 : .5m);
                case "/api/v1/asr/tasks/task/retry": Retries++; Failed = false; return TaskResponse("queued", 0);
                case "/api/v1/asr/tasks/task/cancel": Cancels++; return TaskResponse("cancelled", 0);
                case "/api/v1/asr/tasks/task/result": return Json(new { transcript = "subtitle" });
                default: throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }
        }
        private HttpResponseMessage Upload() => Json(new { id = "upload", offset = uploaded, bytes = 1048576 + 100, assetId = (string?)null });
        private static HttpResponseMessage TaskResponse(string state, decimal charge) => Json(new { id = "task", state, progress = "等待转写", stage = "queued", fraction = 0, completedSegments = 0, totalSegments = 0, priceCredits = .5m, heldCredits = 0, chargedCredits = charge, error = (object?)null });
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
        public void Dispose() { client.Dispose(); authentication.Dispose(); http.Dispose(); Stop.Dispose(); Directory.Delete(root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
    private sealed class Tokens : ICloudTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken ct = default) => Task.FromResult<string?>("refresh");
        public Task WriteAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
