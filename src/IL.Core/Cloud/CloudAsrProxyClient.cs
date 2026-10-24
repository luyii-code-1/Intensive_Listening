using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IL.Core.Asr;
using IL.Core.Audio;
using IL.Core.Ilp;
using IL.Core.Infrastructure;

namespace IL.Core.Cloud;

public sealed record CloudProxyConfiguration(string Model, decimal CreditsPerMinute, string Rounding, int SampleRate,
    int Channels, int BitsPerSample, int MaxSegmentSeconds, int MaxAudioSeconds, long MaxSegmentBytes, int MaxSegments, int UploadConcurrency);
public sealed record CloudProxySegmentState(int Index, string State, int StatusCode);
public sealed record CloudProxyBatch(string Id, string State, decimal PriceCredits, decimal HeldCredits, decimal ChargedCredits,
    CloudProxySegmentState[] Segments, string? Error);

public sealed class CloudAsrProxyClient : IDisposable
{
    private readonly CloudApiTransport _api;
    private readonly string _root;
    private readonly VadSlicer _slicer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record User(string Id);
    internal sealed record Segment(string Sha256, long Bytes, long SampleCount);
    private sealed record Session(string AudioHash, string OperationId, string? BatchId, Segment[] Segments);
    public CloudAsrProxyClient(CloudAuthClient authentication, string dataDirectory, HttpClient? http = null, VadSlicer? slicer = null)
    { _api = new(authentication, http); _root = Path.Combine(dataDirectory, "cloud-asr-proxy"); _slicer = slicer ?? new(); }

    public async Task<CloudProxyConfiguration> ConfigurationAsync(CancellationToken ct = default)
    {
        var config = await SendAsync<CloudProxyConfiguration>(HttpMethod.Get, "asr/proxy/config", null, null, ct);
        if (config.SampleRate != 16000 || config.Channels != 1 || config.BitsPerSample != 16 || config.Rounding != "ceil_total_minutes" ||
            config.CreditsPerMinute <= 0 || config.MaxSegments is < 1 or > 128 || config.MaxSegmentSeconds is < 1 or > 300 ||
            config.MaxSegmentBytes is < 44 or > 10 * 1024 * 1024 || config.MaxAudioSeconds is < 1 or > 1800 || config.UploadConcurrency is < 1 or > 2)
            throw new CloudApiException("proxy_config", "云服务音频配置不受当前客户端支持。");
        return config;
    }
    public async Task<string> RunAsync(string localJobId, string audio, IProgress<AsrProgress>? progress, CancellationToken ct,
        Func<bool> userCancellation, Action<decimal> settled, Func<Task<bool>>? confirmForcedCuts = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await RunOnceAsync(localJobId, audio, progress, ct, userCancellation, settled, confirmForcedCuts); }
            catch (Exception error) when (attempt < 2 && !ct.IsCancellationRequested && IsNetworkInterruption(error))
            {
                AppLog.Warning($"云端转写连接中断，准备恢复原任务 job={localJobId} retry={attempt + 1}", error);
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct);
                // Reload the saved batch and query server state before uploading pending fragments.
            }
        }
    }

    private static bool IsNetworkInterruption(Exception error) => error is HttpRequestException or TaskCanceledException ||
        error is CloudAsrWaitingInterruptedException { InnerException: HttpRequestException or OperationCanceledException };

    private async Task<string> RunOnceAsync(string localJobId, string audio, IProgress<AsrProgress>? progress, CancellationToken ct,
        Func<bool> userCancellation, Action<decimal> settled, Func<Task<bool>>? confirmForcedCuts)
    {
        await _gate.WaitAsync(ct);
        Session? session = null; string? directory = null;
        try
        {
            var configuration = await ConfigurationAsync(ct);
            var user = await SendAsync<User>(HttpMethod.Get, "auth/me", null, null, ct);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.Id + ":" + localJobId)));
            directory = Path.Combine(_root, key); Directory.CreateDirectory(directory);
            var statePath = Path.Combine(directory, "session.json");
            string hash;
            await using (var source = File.OpenRead(audio)) hash = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
            if (File.Exists(statePath)) session = JsonSerializer.Deserialize<Session>(await File.ReadAllTextAsync(statePath, ct), Json);
            if (session is not null && session.AudioHash != hash) throw new AsrException("原任务音频已更改，请新建转写任务。");
            if (session is null)
            {
                progress?.Report(new(AsrStage.Decoding));
                var chunks = Path.Combine(directory, "segments");
                // An interrupted conversion has no submitted batch; replace only its own generated WAVs.
                if (Directory.Exists(chunks)) foreach (var file in Directory.EnumerateFiles(chunks, "chunk_*.wav")) File.Delete(file);
                VadSliceResult sliced;
                var maximum = TimeSpan.FromSeconds(Math.Min(120, configuration.MaxSegmentSeconds));
                try { sliced = await _slicer.SliceAsync(audio, chunks, maxSegmentDuration: maximum, ct: ct); }
                catch (NoSilenceCutException)
                {
                    if (confirmForcedCuts is null || !await confirmForcedCuts()) throw new OperationCanceledException(ct);
                    sliced = await _slicer.SliceAsync(audio, chunks, maxSegmentDuration: maximum, forceCutOnNoSilence: true, ct: ct);
                }
                var manifest = new List<Segment>();
                foreach (var slice in sliced.Slices) manifest.Add(await ReadSegmentAsync(slice.File, ct));
                if (manifest.Count == 0 || manifest.Count > configuration.MaxSegments || manifest.Sum(s => s.SampleCount) > (long)configuration.MaxAudioSeconds * configuration.SampleRate ||
                    manifest.Any(s => s.Bytes > configuration.MaxSegmentBytes || s.SampleCount > (long)configuration.MaxSegmentSeconds * configuration.SampleRate))
                    throw new AsrException("音频超出云服务当前时长或分片限制。");
                session = new(hash, Guid.NewGuid().ToString("N"), null, manifest.ToArray());
                await SaveAsync(statePath, session);
            }
            CloudProxyBatch batch;
            if (session.BatchId is null)
            {
                ct.ThrowIfCancellationRequested();
                batch = await SendAsync<CloudProxyBatch>(HttpMethod.Post, "asr/proxy/batches", JsonContent.Create(new { segments = session.Segments, language = "en" }, options: Json), session.OperationId, CancellationToken.None);
                session = session with { BatchId = batch.Id }; await SaveAsync(statePath, session);
            }
            else batch = await BatchAsync(session.BatchId, ct);
            if (Active(batch))
            {
                var pending = batch.Segments.Where(s => s.State == "pending").Select(s => s.Index).ToArray();
                if (batch.Segments.Length != session.Segments.Length || pending.Any(i => i < 0 || i >= session.Segments.Length)) throw new CloudApiException("proxy_response", "云服务返回的分片清单无效。");
                var sent = 0;
                await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = configuration.UploadConcurrency, CancellationToken = ct }, async (index, token) =>
                {
                    var file = Path.Combine(directory, "segments", $"chunk_{index:000}.wav");
                    var actual = await ReadSegmentAsync(file, token);
                    if (actual != session.Segments[index]) throw new AsrException("本地分片已更改，请新建转写任务。");
                    await using var input = File.OpenRead(file);
                    using var form = new MultipartFormDataContent(); form.Add(new StreamContent(input), "file", Path.GetFileName(file));
                    await SendAsync<CloudProxyBatch>(HttpMethod.Post, $"asr/proxy/batches/{Uri.EscapeDataString(session.BatchId!)}/segments/{index}", form, null, token, TimeSpan.FromMinutes(5));
                    var completed = Interlocked.Increment(ref sent); progress?.Report(new(AsrStage.Uploading, (double)completed / Math.Max(1, pending.Length), SegmentIndex: completed, SegmentTotal: session.Segments.Length));
                });
                // Result endpoints are unavailable until the whole batch settles. Upload all fragments first.
                do
                {
                    ct.ThrowIfCancellationRequested(); batch = await BatchAsync(session.BatchId!, ct);
                    var completed = batch.Segments.Count(s => s.State == "succeeded");
                    progress?.Report(new(AsrStage.Recognizing, (double)completed / session.Segments.Length, SegmentIndex: completed, SegmentTotal: session.Segments.Length));
                    if (Active(batch)) await Task.Delay(TimeSpan.FromSeconds(2), ct);
                } while (Active(batch));
            }
            settled(batch.ChargedCredits);
            if (batch.State == "succeeded") return await ReadResultAsync(batch, session, directory, progress, ct);
            if (batch.State == "cancelled") { File.Delete(statePath); throw new CloudAsrCancelledException(); }
            await SaveFailureResponsesAsync(batch, directory, ct);
            File.Delete(statePath);
            throw new AsrException("云端转写未完成，已释放整单冻结积分。" + (string.IsNullOrWhiteSpace(batch.Error) ? "" : " " + batch.Error));
        }
        catch (CloudAsrCancelledException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && userCancellation())
        {
            if (session?.BatchId is null || directory is null) throw;
            try
            {
                var batch = await SendAsync<CloudProxyBatch>(HttpMethod.Post, $"asr/proxy/batches/{Uri.EscapeDataString(session.BatchId)}/cancel", null, null, CancellationToken.None);
                settled(batch.ChargedCredits);
                if (batch.State == "succeeded") throw new CloudAsrCompletedAfterCancellationException(await ReadResultAsync(batch, session, directory, progress, CancellationToken.None));
                if (Active(batch)) throw new CloudAsrWaitingInterruptedException("取消尚未确认，重试可查询原整单。");
                File.Delete(Path.Combine(directory, "session.json"));
            }
            catch (CloudAsrCompletedAfterCancellationException) { throw; }
            catch (Exception error) { throw new CloudAsrWaitingInterruptedException("取消结果暂未确认，重试可继续查询原整单。", error); }
            throw;
        }
        catch (CloudApiException) when (session?.BatchId is not null && directory is not null)
        {
            try
            {
                var failed = await BatchAsync(session.BatchId, CancellationToken.None);
                if (!Active(failed)) { settled(failed.ChargedCredits); if (failed.State != "succeeded") File.Delete(Path.Combine(directory, "session.json")); }
            }
            catch (Exception) { }
            throw;
        }
        catch (Exception error) when (session is not null && (error is OperationCanceledException or HttpRequestException or IOException))
        {
            var state = session.BatchId is null ? "提交结果尚未确认，重试会使用原请求继续确认。" : "云端整单保留；重试会继续原整单。";
            var reason = error is OperationCanceledException ? "连接超时或等待被中断。" : error is HttpRequestException ? "云服务网络连接中断。" : "传输或本地文件读写失败。";
            throw new CloudAsrWaitingInterruptedException(reason + state, error);
        }
        finally { _gate.Release(); }
    }
    private Task<CloudProxyBatch> BatchAsync(string id, CancellationToken ct) => SendAsync<CloudProxyBatch>(HttpMethod.Get, "asr/proxy/batches/" + Uri.EscapeDataString(id), null, null, ct);
    private static bool Active(CloudProxyBatch batch) => batch.State is "queued" or "running";
    private async Task<string> ReadResultAsync(CloudProxyBatch batch, Session session, string directory, IProgress<AsrProgress>? progress, CancellationToken ct)
    {
        var cues = new List<SrtCue>(); long samples = 0;
        for (var index = 0; index < session.Segments.Length; index++)
        {
            var (status, raw) = await RawAsync(batch.Id, index, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{index}.response.json"), raw, ct);
            if (status is < 200 or >= 300) throw new AsrException($"供应商分片响应失败（HTTP {status}）。原始响应已保留。");
            var transcript = AsrTranscription.FromDashScopeResponseBody(raw);
            var offset = TimeSpan.FromSeconds(samples / 16000d);
            cues.AddRange(transcript.Cues.Select(c => c with { Start = c.Start + offset, End = c.End + offset }));
            samples += session.Segments[index].SampleCount;
        }
        progress?.Report(new(AsrStage.Merging));
        return SrtParser.Serialize(SegmentedAsrRunner.Normalize(cues)).TrimEnd();
    }
    private async Task SaveFailureResponsesAsync(CloudProxyBatch batch, string directory, CancellationToken ct)
    {
        foreach (var part in batch.Segments.Where(s => s.State == "failed" && s.StatusCode > 0))
        {
            var (status, body) = await RawAsync(batch.Id, part.Index, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{part.Index}.error.json"), JsonSerializer.Serialize(new { status, body }), ct);
        }
    }
    private async Task<(int Status, string Body)> RawAsync(string id, int index, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(CloudAuthClient.Server, $"api/v1/asr/proxy/batches/{Uri.EscapeDataString(id)}/segments/{index}/result"));
        using var response = await _api.SendAsync(request, deadline.Token, invalidateUnauthorized: false);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(deadline.Token));
    }
    internal static async Task<Segment> ReadSegmentAsync(string file, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(file, ct);
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - 8)
            throw new AsrException("分片不是完整的 PCM WAV。");
        var pcm = false; long samples = 0;
        for (var offset = 12; offset < bytes.Length;)
        {
            if (offset + 8 > bytes.Length) throw new AsrException("WAV 分片不完整。");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            if (size > bytes.Length - offset - 8) throw new AsrException("WAV 分片不完整。");
            var data = bytes.AsSpan(offset + 8, (int)size);
            if (bytes.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                if (pcm || size < 16 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 16000 || BinaryPrimitives.ReadUInt16LittleEndian(data[12..]) != 2 || BinaryPrimitives.ReadUInt16LittleEndian(data[14..]) != 16)
                    throw new AsrException("分片需要 16kHz 单声道 PCM16 WAV。");
                pcm = true;
            }
            if (bytes.AsSpan(offset, 4).SequenceEqual("data"u8)) { if (!pcm || size % 2 != 0) throw new AsrException("WAV 采样数据无效。"); samples += size / 2; }
            offset = checked(offset + 8 + (int)size + ((int)size & 1));
        }
        if (!pcm || samples <= 0) throw new AsrException("WAV 分片没有有效采样数据。");
        return new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength, samples);
    }
    private static async Task SaveAsync(string path, Session session)
    { await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(session, Json)); File.Move(path + ".tmp", path, true); }
    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, string? key, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, new Uri(CloudAuthClient.Server, "api/v1/" + path)) { Content = content };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await _api.SendAsync(request, deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(Json, deadline.Token);
            var code = error.TryGetProperty("error", out var kind) ? kind.GetString() ?? "http" : "http";
            if (code == "scope") throw new CloudAuthenticationException("scope", "请重新登录 Intensive Listening 云服务，授权云端转写。");
            throw new CloudApiException(code, error.TryGetProperty("message", out var message) ? message.GetString() ?? "云服务请求失败。" : "云服务请求失败。");
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, deadline.Token) ?? throw new CloudApiException("proxy_response", "云服务返回的数据无效。");
    }
    public void Dispose() => _api.Dispose();
}
