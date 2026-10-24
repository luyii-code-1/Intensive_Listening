using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IL.Core.Asr;

namespace IL.Core.Cloud;

public sealed record CloudAsrTask(string Id, string State, JsonElement? Progress, decimal PriceCredits, decimal HeldCredits, decimal ChargedCredits, JsonElement? Error);
public sealed class CloudAsrCancelledException : OperationCanceledException { }
public sealed class CloudAsrWaitingInterruptedException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class CloudAsrCompletedAfterCancellationException(string transcript) : Exception
{
    public string Transcript { get; } = transcript;
}

public sealed class CloudAsrClient : IDisposable
{
    private readonly CloudApiTransport _api;
    private readonly string _root;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Asset(string Id);
    private sealed record Result(string Transcript);
    private sealed record User(string Id);
    private sealed record Session(string AssetId, string OperationId, string? TaskId, string Sha256);
    public CloudAsrClient(CloudAuthClient authentication, string dataDirectory, HttpClient? http = null)
    { _api = new(authentication, http); _root = Path.Combine(dataDirectory, "cloud-asr"); }

    public async Task<string> RunAsync(string localJobId, string audio, IProgress<AsrProgress>? progress,
        CancellationToken ct, Func<bool> userCancellation, Action<decimal> settled)
    {
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
                progress?.Report(new(AsrStage.Uploading));
                await using var file = File.OpenRead(audio);
                using var form = new MultipartFormDataContent();
                form.Add(new StreamContent(file), "file", Path.GetFileName(audio));
                var asset = await SendAsync<Asset>(HttpMethod.Post, "asr/assets", form, null, ct, TimeSpan.FromMinutes(10));
                session = new(asset.Id, Guid.NewGuid().ToString("N"), null, hash);
                await SaveAsync(path, session);
            }
            if (session.TaskId is null)
            {
                ct.ThrowIfCancellationRequested();
                // Once submission starts, finish the acceptance handshake so a user cancellation can address the task.
                var task = await SendAsync<CloudAsrTask>(HttpMethod.Post, "asr/tasks", JsonContent.Create(new { audioAssetId = session.AssetId }), session.OperationId, CancellationToken.None);
                session = session with { TaskId = task.Id }; await SaveAsync(path, session);
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var task = await SendAsync<CloudAsrTask>(HttpMethod.Get, "asr/tasks/" + Uri.EscapeDataString(session.TaskId), null, null, ct);
                if (task.State is "succeeded" or "failed" or "cancelled")
                {
                    settled(task.ChargedCredits);
                    if (task.State == "succeeded") return await ResultAsync(task.Id, ct);
                    File.Delete(path);
                    if (task.State == "cancelled") throw new CloudAsrCancelledException();
                    throw new AsrException("云端转写失败，未收取转写费用。" + SafeError(task.Error));
                }
                progress?.Report(new(AsrStage.Recognizing, task.Progress is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var fraction) ? fraction : null));
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (CloudAsrCancelledException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && userCancellation())
        {
            if (session?.TaskId is null) throw;
            try
            {
                var task = await SendAsync<CloudAsrTask>(HttpMethod.Post, "asr/tasks/" + Uri.EscapeDataString(session.TaskId) + "/cancel", null, null, CancellationToken.None);
                settled(task.ChargedCredits);
                if (task.State == "succeeded") throw new CloudAsrCompletedAfterCancellationException(await ResultAsync(task.Id, CancellationToken.None));
                if (task.State != "cancelled" && task.State != "failed") throw new AsrException("取消尚未确认，请重试查询原任务。");
                if (path is not null) File.Delete(path);
            }
            catch (CloudAsrCompletedAfterCancellationException) { throw; }
            catch (Exception error) { throw new CloudAsrWaitingInterruptedException("取消尚未确认，重试可查询原任务。费用以服务端终态为准。", error); }
            throw;
        }
        catch (Exception error) when (session is not null && (error is OperationCanceledException or HttpRequestException))
        { throw new CloudAsrWaitingInterruptedException("等待已中断。云端任务保留，重试可继续查询。", error); }
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
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, deadline.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && body.TryGetProperty("error", out var scope) && scope.GetString() == "scope")
                throw new CloudAuthenticationException("scope", "请在设置中重新登录 Intensive Listening 云服务，授权云端转写。此次未提交新的转写任务。");
            throw new CloudApiException(body.TryGetProperty("error", out var code) ? code.GetString() ?? "http" : "http",
                body.TryGetProperty("message", out var message) ? message.GetString() ?? "云服务请求失败。" : "云服务请求失败。");
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, deadline.Token) ?? throw new CloudApiException("invalid_response", "云服务返回的数据无效。");
    }
    public void Dispose() => _api.Dispose();
}
