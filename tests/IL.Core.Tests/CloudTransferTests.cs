using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using IL.Core.Cloud;
using Xunit;

namespace IL.Core.Tests;

public sealed class CloudTransferTests
{
    [Fact]
    public async Task BearerTransportRejectsForeignOriginsBeforeReadingCredentials()
    {
        var store = new Tokens();
        using var authentication = new CloudAuthClient(store);
        using var api = new CloudApiTransport(authentication);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://objects.example.invalid/private");
        await Assert.ThrowsAsync<ArgumentException>(() => api.SendAsync(request));
        Assert.Equal(0, store.Reads);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task InterruptedDownloadPersistsAuthorizationAndResumesAfterClientRecreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-cloud-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] payload = [1, 2, 3, 4, 5, 6];
            var offers = 0; var starts = 0; var reads = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/connect/token": return Json(new { access_token = "access", refresh_token = "rotated", token_type = "Bearer", expires_in = 900, device_id = "device" });
                    case "/api/v1/auth/me": return Json(new { id = "user", email = "user@example.invalid", name = "User", slots = 2, emailVerified = true });
                    case "/api/v1/library/item/downloads":
                        offers++; Assert.Single(request.Headers.GetValues("Idempotency-Key"));
                        return Json(new { id = "grant", expires = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds() });
                    case "/api/v1/library/downloads/grant/start":
                        starts++;
                        return Json(new { url = "/api/v1/file-content", sha256 = Convert.ToHexString(SHA256.HashData(payload)), bytes = payload.Length, fileName = "lesson.ilp", expires = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds() });
                    case "/api/v1/file-content":
                        reads++;
                        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                        if (reads == 1)
                        {
                            Assert.Null(request.Headers.Range);
                            var broken = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(payload[..3])) };
                            broken.Content.Headers.ContentLength = payload.Length;
                            return broken;
                        }
                        Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
                        var resumed = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload[3..]) };
                        resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
                        return resumed;
                    default: throw new InvalidOperationException("Unexpected test endpoint");
                }
            }));
            using var authentication = new CloudAuthClient(new Tokens(), http);
            using (var first = new CloudResources(authentication, root, http))
                await Assert.ThrowsAsync<IOException>(() => first.DownloadAsync("item", "file", "ilp", null));
            using var second = new CloudResources(authentication, root, http);
            var file = await second.DownloadAsync("item", "file", "ilp", null);
            Assert.Equal(payload, await File.ReadAllBytesAsync(file));
            Assert.Equal(1, offers);
            Assert.Equal(2, starts);
            Assert.Equal(2, reads);
            Assert.Equal(file, await second.DownloadAsync("item", "file", "ilp", null));
            Assert.Equal(2, reads);
            await second.CompleteDownloadAsync("item", "file", "ilp");
            Assert.False(File.Exists(file));
        }
        finally { Directory.Delete(root, true); }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class Tokens : ICloudTokenStore
    {
        public int Reads;
        public Task<string?> ReadAsync(CancellationToken ct = default) { Reads++; return Task.FromResult<string?>("initial-refresh"); }
        public Task WriteAsync(string refreshToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class InterruptedStream(byte[] first) : Stream
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) throw new IOException("Simulated disconnect");
            _read = true; first.AsMemory().CopyTo(buffer); return ValueTask.FromResult(first.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
