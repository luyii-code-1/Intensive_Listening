using System.Net;
using System.Text.Json;
using IL.Core.Cloud;
using Xunit;

namespace IL.Core.Tests;

public sealed class CloudAsrClientTests
{
    [Fact]
    public async Task NetworkFailurePreservesTaskAndRetryUsesSameUploadAndSubmission()
    {
        using var fixture = new Fixture();
        fixture.FailStatusOnce = true;
        await Assert.ThrowsAsync<CloudAsrWaitingInterruptedException>(() => fixture.RunAsync());
        Assert.Equal(0, fixture.CancelCalls);
        Assert.Equal("subtitle", await fixture.RunAsync());
        Assert.Equal(1, fixture.Uploads); Assert.Equal(1, fixture.Submissions);
        Assert.Equal(5m, fixture.Charged);
    }

    [Fact]
    public async Task PolicyInterruptionDoesNotRequestBillableCancellation()
    {
        using var fixture = new Fixture();
        fixture.OnAccepted = fixture.Stop.Cancel;
        await Assert.ThrowsAsync<CloudAsrWaitingInterruptedException>(() => fixture.RunAsync());
        Assert.Equal(0, fixture.CancelCalls);
        Assert.Null(fixture.Charged);
    }

    [Fact]
    public async Task UserCancellationUsesAuthoritativeSettlement()
    {
        using var fixture = new Fixture { UserCancellation = true, CancellationState = "cancelled" };
        fixture.OnAccepted = fixture.Stop.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());
        Assert.Equal(1, fixture.CancelCalls); Assert.Equal(2m, fixture.Charged);
    }

    [Fact]
    public async Task CompletionWinningCancellationKeepsSuccessfulTranscriptAndCharge()
    {
        using var fixture = new Fixture { UserCancellation = true, CancellationState = "succeeded" };
        fixture.OnAccepted = fixture.Stop.Cancel;
        var result = await Assert.ThrowsAsync<CloudAsrCompletedAfterCancellationException>(() => fixture.RunAsync());
        Assert.Equal("subtitle", result.Transcript); Assert.Equal(5m, fixture.Charged);
        Assert.Equal(1, fixture.CancelCalls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "il-cloud-asr-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly CloudAuthClient _auth;
        private readonly CloudAsrClient _client;
        public CancellationTokenSource Stop { get; } = new();
        public bool UserCancellation, FailStatusOnce;
        public string CancellationState = "cancelled";
        public Action? OnAccepted;
        public int Uploads, Submissions, CancelCalls;
        public decimal? Charged;
        public Fixture()
        {
            Directory.CreateDirectory(_root); File.WriteAllText(Path.Combine(_root, "test.mp3"), "audio");
            _http = new(new Handler(Request)); _auth = new(new Tokens(), _http); _client = new(_auth, _root, _http);
        }
        public Task<string> RunAsync() => _client.RunAsync("local-job", Path.Combine(_root, "test.mp3"), null, Stop.Token, () => UserCancellation, c => Charged = c);
        private HttpResponseMessage Request(HttpRequestMessage request)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/connect/token": return Json(new { access_token = "access", refresh_token = "rotated", token_type = "Bearer", expires_in = 900 });
                case "/api/v1/auth/me": return Json(new { id = "user" });
                case "/api/v1/asr/assets": Uploads++; return Json(new { id = "asset" });
                case "/api/v1/asr/tasks":
                    Submissions++; Assert.Single(request.Headers.GetValues("Idempotency-Key")); OnAccepted?.Invoke(); return TaskResponse("queued", 0);
                case "/api/v1/asr/tasks/task":
                    if (FailStatusOnce) { FailStatusOnce = false; throw new HttpRequestException("offline"); }
                    return TaskResponse("succeeded", 5);
                case "/api/v1/asr/tasks/task/cancel": CancelCalls++; return TaskResponse(CancellationState, CancellationState == "succeeded" ? 5 : 2);
                case "/api/v1/asr/tasks/task/result": return Json(new { transcript = "subtitle" });
                default: throw new InvalidOperationException("Unexpected endpoint");
            }
        }
        private static HttpResponseMessage TaskResponse(string state, decimal charge) => Json(new { id = "task", state, progress = "正在转写", priceCredits = 5, heldCredits = 0, chargedCredits = charge, error = (object?)null });
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
        public void Dispose() { _client.Dispose(); _auth.Dispose(); _http.Dispose(); Stop.Dispose(); Directory.Delete(_root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
    private sealed class Tokens : ICloudTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken ct = default) => Task.FromResult<string?>("refresh");
        public Task WriteAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
