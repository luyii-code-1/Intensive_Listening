using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IL.Core.Asr;

namespace IL.Core.Cloud;

public sealed record DesktopAsrConfiguration(string Protocol, string Model, decimal CreditsPerMinute, string Rounding, int MaxAudioSeconds, long MaxAudioBytes, int ChunkBytes, bool Paused);
public sealed record DesktopAsrTask(string Id, string State, string Progress, string Stage, double Fraction, int CompletedSegments, int TotalSegments, decimal PriceCredits, decimal HeldCredits, decimal ChargedCredits, JsonElement? Error);
public sealed class DesktopCloudAsrClient : IDisposable
{
    private readonly CloudApiTransport _api;
    private readonly string _root;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Result(string Transcript);
    private sealed record User(string Id);
    private sealed record Session(string? AssetId, string OperationId, string? TaskId, string Sha256, string? UploadId = null, string? RetryKey = null);
    private sealed record Upload(string Id, long Offset, long Bytes, string? AssetId);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public DesktopCloudAsrClient(CloudAuthClient authentication, string dataDirectory, HttpClient? http = null)
    { _api = new(authentication, http); _root = Path.Combine(dataDirectory, "desktop-cloud-asr-v2"); }

    public async Task<string> RunAsync(string localJobId, string audio, IProgress<AsrProgress>? progress,
        CancellationToken ct, Func<bool> userCancellation, Action<decimal> settled)
    {
        await _gate.WaitAsync(ct);
        Session? session = null; string? path = null;
        try
        {
            var user = await SendAsync<User>(HttpMethod.Get, "auth/me", null, null, ct);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.Id + ":" + localJobId)));
            Directory.CreateDirectory(_root); path = Path.Combine(_root, identity + ".json");
            string hash;
            await using (var file = File.OpenRead(audio)) hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            session = File.Exists(path) ? JsonSerializer.Deserialize<Session>(await File.ReadAllTextAsync(path, ct), Json) : null;
            if (session is not null && session.Sha256 != hash) throw new AsrException("原任务的音频已更改，请建立新的转写任务。");
            if (session is null)
            {
                session = new(null, Guid.NewGuid().ToString("N"), null, hash);
                await SaveAsync(path, session);
            }
            if (session.AssetId is null)
            {
                progress?.Report(new(AsrStage.Uploading, Detail: "正在准备上传完整音频。"));
                var config = await ConfigurationAsync(ct);
                if (config.Paused) throw new CloudAsrWaitingInterruptedException("云端维护中，稍后重试可继续上传。");
                var length = new FileInfo(audio).Length;
                if (length <= 0 || length > config.MaxAudioBytes) throw new AsrException($"音频文件须大于 0 且不超过 {config.MaxAudioBytes / 1048576} MB。");
                var upload = session.UploadId is null
                    ? await SendAsync<Upload>(HttpMethod.Post, "asr/desktop/uploads", JsonContent.Create(new { name = Path.GetFileName(audio), bytes = length, sha256 = hash }), session.OperationId, ct)
                    : await SendAsync<Upload>(HttpMethod.Get, "asr/desktop/uploads/" + session.UploadId, null, null, ct);
                session = session with { UploadId = upload.Id }; await SaveAsync(path, session);
                await using var input = File.OpenRead(audio);
                if (upload.Bytes != length || upload.Offset < 0 || upload.Offset > length) throw new AsrException("云端上传状态与音频不一致。");
                progress?.Report(new(AsrStage.Uploading, (double)upload.Offset / length, upload.Offset, length));
                var buffer = new byte[config.ChunkBytes];
                while (upload.Offset < length && upload.AssetId is null)
                {
                    if (upload.Offset < 0 || upload.Offset > length) throw new AsrException("云端上传位置无效。");
                    input.Position = upload.Offset;
                    var count = await input.ReadAsync(buffer, ct);
                    using var content = new ByteArrayContent(buffer, 0, count);
                    content.Headers.ContentType = new("application/octet-stream");
                    try { upload = await SendAsync<Upload>(HttpMethod.Put, $"asr/desktop/uploads/{upload.Id}?offset={upload.Offset}", content, null, ct, TimeSpan.FromMinutes(2)); }
                    catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !ct.IsCancellationRequested || e is CloudApiException { Code: "upload_offset" })
                    {
                        progress?.Report(new(AsrStage.Uploading, (double)upload.Offset / length, upload.Offset, length, Detail: "正在恢复上传连接，已上传的内容会保留。"));
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                        upload = await ReconnectAsync(() => SendAsync<Upload>(HttpMethod.Get, "asr/desktop/uploads/" + upload.Id, null, null, ct), progress, ct);
                    }
                    progress?.Report(new(AsrStage.Uploading, (double)upload.Offset / length, upload.Offset, length));
                }
                upload = await SendAsync<Upload>(HttpMethod.Post, $"asr/desktop/uploads/{upload.Id}/complete", null, null, ct, TimeSpan.FromMinutes(3));
                if (string.IsNullOrWhiteSpace(upload.AssetId)) throw new AsrException("服务器尚未确认音频上传。");
                session = session with { AssetId = upload.AssetId }; await SaveAsync(path, session);
            }
            if (session.TaskId is null)
            {
                ct.ThrowIfCancellationRequested();
                // Once submission starts, finish the acceptance handshake so a user cancellation can address the task.
                var task = await SendAsync<DesktopAsrTask>(HttpMethod.Post, "asr/tasks", JsonContent.Create(new { audioAssetId = session.AssetId, desktopV2 = true }), session.OperationId, CancellationToken.None);
                session = session with { TaskId = task.Id }; await SaveAsync(path, session);
            }
            if (session.RetryKey != null)
            {
                var previous = await SendAsync<DesktopAsrTask>(HttpMethod.Get, "asr/tasks/" + session.TaskId, null, null, ct);
                if (previous.State is "failed" or "cancelled")
                    await SendAsync<DesktopAsrTask>(HttpMethod.Post, $"asr/tasks/{session.TaskId}/retry", null, session.RetryKey, ct);
                session = session with { RetryKey = null }; await SaveAsync(path, session);
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var task = await ReconnectAsync(() => SendAsync<DesktopAsrTask>(HttpMethod.Get, "asr/tasks/" + Uri.EscapeDataString(session.TaskId), null, null, ct), progress, ct);
                if (task.State is "succeeded" or "failed" or "cancelled")
                {
                    settled(task.ChargedCredits);
                    if (task.State == "succeeded") return await ResultAsync(task.Id, ct);
                    if (task.State == "cancelled") File.Delete(path);
                    if (task.State == "cancelled") throw new CloudAsrCancelledException();
                    session = session with { RetryKey = Guid.NewGuid().ToString("N") }; await SaveAsync(path, session);
                    throw new AsrException("云端转写失败，未收取转写费用。" + SafeError(task.Error));
                }
                var stage = task.State == "queued" ? AsrStage.Queued : task.Stage switch
                { "slicing" => AsrStage.Slicing, "merging" => AsrStage.Merging, _ => AsrStage.Recognizing };
                progress?.Report(new(stage, task.Fraction, SegmentIndex: task.CompletedSegments, SegmentTotal: task.TotalSegments > 0 ? task.TotalSegments : null, Detail: task.Progress));
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (CloudAsrCancelledException) { throw; }
        catch (CloudApiException error) when (session is { TaskId: null } && path != null && error.Code is "upload" or "upload_expired" or "upload_hash" or "asset")
        {
            File.Delete(path);
            throw new CloudAsrWaitingInterruptedException("上传会话已失效，重试将重新上传完整音频。", error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && userCancellation())
        {
            if (session is null) throw;
            try
            {
                if (session.TaskId is null && session.AssetId is null)
                {
                    if (session.UploadId != null) await SendAsync<JsonElement>(HttpMethod.Delete, "asr/desktop/uploads/" + session.UploadId, null, null, CancellationToken.None);
                    if (path != null) File.Delete(path);
                    throw new CloudAsrCancelledException();
                }
                if (session.TaskId is null)
                {
                    var accepted = await SendAsync<DesktopAsrTask>(HttpMethod.Post, "asr/tasks", JsonContent.Create(new { audioAssetId = session.AssetId, desktopV2 = true }), session.OperationId, CancellationToken.None);
                    session = session with { TaskId = accepted.Id }; if (path != null) await SaveAsync(path, session);
                }
                var task = await SendAsync<DesktopAsrTask>(HttpMethod.Post, "asr/tasks/" + Uri.EscapeDataString(session.TaskId) + "/cancel", null, null, CancellationToken.None);
                settled(task.ChargedCredits);
                if (task.State == "succeeded") throw new CloudAsrCompletedAfterCancellationException(await ResultAsync(task.Id, CancellationToken.None));
                if (task.State != "cancelled" && task.State != "failed") throw new AsrException("取消尚未确认，请重试查询原任务。");
                if (path is not null && task.State == "cancelled") File.Delete(path);
                if (path is not null && task.State == "failed")
                {
                    session = session with { RetryKey = Guid.NewGuid().ToString("N") }; await SaveAsync(path, session);
                }
            }
            catch (CloudAsrCancelledException) { throw; }
            catch (CloudAsrCompletedAfterCancellationException) { throw; }
            catch (Exception error) { throw new CloudAsrWaitingInterruptedException("取消尚未确认，重试可查询原任务。费用以服务端终态为准。", error); }
            throw;
        }
        catch (Exception error) when (session is not null && (error is OperationCanceledException or HttpRequestException or IOException))
        { throw new CloudAsrWaitingInterruptedException("等待已中断。云端任务保留，重试可继续查询。", error); }
        finally { _gate.Release(); }
    }
    public async Task<DesktopAsrConfiguration> ConfigurationAsync(CancellationToken ct = default)
    {
        var config = await SendAsync<DesktopAsrConfiguration>(HttpMethod.Get, "asr/desktop/config", null, null, ct);
        if (config.Protocol != "desktop-asr-v2" || config.ChunkBytes is < 65536 or > 1048576 || config.MaxAudioSeconds is < 1 or > 1800 || config.MaxAudioBytes <= 0 || config.Rounding != "ceil_total_minutes" || config.CreditsPerMinute <= 0)
            throw new CloudApiException("asr_config", "云服务转写配置无效，请联系管理员。");
        return config;
    }
    public async Task CancelAsync(string localJobId, Action<decimal> settled, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var user = await SendAsync<User>(HttpMethod.Get, "auth/me", null, null, ct);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.Id + ":" + localJobId)));
            var path = Path.Combine(_root, identity + ".json");
            if (!File.Exists(path)) return;
            var session = JsonSerializer.Deserialize<Session>(await File.ReadAllTextAsync(path, ct), Json);
            if (session?.AssetId is null)
            {
                if (session?.UploadId != null) await SendAsync<JsonElement>(HttpMethod.Delete, "asr/desktop/uploads/" + session.UploadId, null, null, ct);
                File.Delete(path); return;
            }
            if (session.TaskId is null)
            {
                // Resolve a potentially lost acceptance with its original key before cancelling.
                var accepted = await SendAsync<DesktopAsrTask>(HttpMethod.Post, "asr/tasks", JsonContent.Create(new { audioAssetId = session.AssetId, desktopV2 = true }), session.OperationId, ct);
                session = session with { TaskId = accepted.Id }; await SaveAsync(path, session);
            }
            var task = await SendAsync<DesktopAsrTask>(HttpMethod.Post, $"asr/tasks/{session.TaskId}/cancel", null, null, ct);
            settled(task.ChargedCredits);
            if (task.State == "succeeded") throw new CloudAsrCompletedAfterCancellationException(await ResultAsync(task.Id, ct));
            if (task.State is not ("cancelled" or "failed")) throw new CloudAsrWaitingInterruptedException("取消尚未确认，云端任务记录保留。");
            if (task.State == "cancelled") File.Delete(path);
            else await SaveAsync(path, session with { RetryKey = Guid.NewGuid().ToString("N") });
        }
        finally { _gate.Release(); }
    }
    private static async Task<T> ReconnectAsync<T>(Func<Task<T>> action, IProgress<AsrProgress>? progress, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await action(); }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !ct.IsCancellationRequested)
            {
                progress?.Report(new(AsrStage.Queued, Detail: "连接暂时中断，正在重连。云端任务和上传记录会保留。"));
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 2 * ++attempt)), ct);
            }
        }
    }
    private async Task<string> ResultAsync(string id, CancellationToken ct) =>
        (await SendAsync<Result>(HttpMethod.Get, "asr/tasks/" + Uri.EscapeDataString(id) + "/result", null, null, ct)).Transcript;
    private static string SafeError(JsonElement? error) => error is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("message", out var message) ? " " + message.GetString() : "";
    private static async Task SaveAsync(string path, Session session)
    {
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(session, Json)); File.Move(path + ".tmp", path, true);
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string endpoint, HttpContent? content, string? key, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, new Uri(CloudAuthClient.Server, "api/v1/" + endpoint)) { Content = content };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await _api.SendAsync(request, deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new HttpRequestException($"云服务暂时不可用（HTTP {(int)response.StatusCode}）。", null, response.StatusCode);
            JsonElement body;
            try { body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, deadline.Token); }
            catch (JsonException) { throw new CloudApiException("http", $"云服务请求失败（HTTP {(int)response.StatusCode}）。"); }
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && body.TryGetProperty("error", out var scope) && scope.GetString() == "scope")
                throw new CloudAuthenticationException("scope", "请在设置中重新登录 Intensive Listening 云服务，授权云端转写后继续任务。");
            throw new CloudApiException(body.TryGetProperty("error", out var code) ? code.GetString() ?? "http" : "http",
                body.TryGetProperty("message", out var message) ? message.GetString() ?? "云服务请求失败。" : "云服务请求失败。");
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, deadline.Token) ?? throw new CloudApiException("invalid_response", "云服务返回的数据无效。");
    }
    public void Dispose() => _api.Dispose();
}
