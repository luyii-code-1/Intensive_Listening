using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IL.Core.Cloud;
using Xunit;

namespace IL.Core.Tests;

public sealed class CloudAsrProxyTests
{
    [Fact]
    public async Task UploadsAllTwelveFragmentsBeforeReadingAnyResultAndKeepsOneBatchAfterDisconnect()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-proxy-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var audio = Path.Combine(root, "source.mp3"); await File.WriteAllTextAsync(audio, "source audio");
            var directory = Path.Combine(root, "cloud-asr-proxy", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("user:job"))));
            Directory.CreateDirectory(Path.Combine(directory, "segments"));
            var fragments = new List<CloudAsrProxyClient.Segment>();
            for (var index = 0; index < 12; index++)
            {
                var file = Path.Combine(directory, "segments", $"chunk_{index:000}.wav");
                await File.WriteAllBytesAsync(file, Wave(16000)); fragments.Add(await CloudAsrProxyClient.ReadSegmentAsync(file, default));
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new
            {
                audioHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(audio))), operationId = "same-operation", batchId = (string?)null, segments = fragments
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var submitted = 0; var uploaded = new ConcurrentDictionary<int, byte>(); var lostResponse = true; var results = 0;
            object Batch() => new { id = "batch", state = uploaded.Count == 12 ? "succeeded" : "queued", priceCredits = .5m, heldCredits = uploaded.Count == 12 ? 0 : .5m, chargedCredits = uploaded.Count == 12 ? .5m : 0,
                segments = Enumerable.Range(0, 12).Select(i => new { index = i, state = uploaded.ContainsKey(i) ? "succeeded" : "pending", statusCode = uploaded.ContainsKey(i) ? 200 : 0 }).ToArray(), error = (string?)null };
            using var http = new HttpClient(new Handler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/connect/token") return Json(new { access_token = "access", refresh_token = "refresh", token_type = "Bearer", expires_in = 900 });
                if (path == "/api/v1/auth/me") return Json(new { id = "user" });
                if (path.EndsWith("/config")) return Json(new { model = "test", creditsPerMinute = .5m, rounding = "ceil_total_minutes", sampleRate = 16000, channels = 1, bitsPerSample = 16, maxSegmentSeconds = 300, maxAudioSeconds = 1800, maxSegmentBytes = 10485760, maxSegments = 128, uploadConcurrency = 2 });
                if (path == "/api/v1/asr/proxy/batches") { Assert.Equal("same-operation", request.Headers.GetValues("Idempotency-Key").Single()); submitted++; return Json(Batch()); }
                if (path == "/api/v1/asr/proxy/batches/batch")
                {
                    if (uploaded.Count == 12 && lostResponse) { lostResponse = false; throw new HttpRequestException("offline after uploads"); }
                    return Json(Batch());
                }
                if (path.EndsWith("/result"))
                {
                    Assert.Equal(12, uploaded.Count); results++;
                    return Json(new { output = new { sentence = new[] { new { words = new[] { new { text = "Hello", begin_time = 0, end_time = 500, punctuation = "." } }, end_time = 500 } } } });
                }
                if (path.Contains("/segments/")) { uploaded.TryAdd(int.Parse(path.Split('/')[^1]), 0); return Json(Batch()); }
                throw new InvalidOperationException("Unexpected endpoint");
            }));
            using var auth = new CloudAuthClient(new Tokens(), http); using var client = new CloudAsrProxyClient(auth, root, http);
            decimal? charged = null;
            await Assert.ThrowsAsync<CloudAsrWaitingInterruptedException>(() => client.RunAsync("job", audio, null, default, () => false, value => charged = value));
            Assert.Equal(0, results);
            var srt = await client.RunAsync("job", audio, null, default, () => false, value => charged = value);
            Assert.Equal(1, submitted); Assert.Equal(12, uploaded.Count); Assert.Equal(12, results); Assert.Equal(.5m, charged);
            Assert.Contains("00:00:11,000 --> 00:00:11,500", srt);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ManifestUsesActualPcmSamplesAndRejectsTruncatedWave()
    {
        var path = Path.GetTempFileName();
        try
        {
            var bytes = Wave(12345); await File.WriteAllBytesAsync(path, bytes);
            var part = await CloudAsrProxyClient.ReadSegmentAsync(path, default);
            Assert.Equal(12345, part.SampleCount); Assert.Equal(bytes.Length, part.Bytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), part.Sha256);
            await File.WriteAllBytesAsync(path, bytes[..^1]);
            await Assert.ThrowsAsync<IL.Core.Asr.AsrException>(() => CloudAsrProxyClient.ReadSegmentAsync(path, default));
        }
        finally { File.Delete(path); }
    }
    private static byte[] Wave(int samples)
    {
        var bytes = new byte[44 + samples * 2]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples * 2); return bytes;
    }
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
    private sealed class Tokens : ICloudTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken ct = default) => Task.FromResult<string?>("refresh");
        public Task WriteAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
