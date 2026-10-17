using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace IL.Core.Cloud;

public sealed record CloudUser(string Id, string Email, string Name, int Slots, bool EmailVerified);
public sealed record CloudStorage(long UsedBytes, long LimitBytes, int UsedSlots, int Slots);
public sealed record CloudWallet(decimal Balance, decimal Held, decimal Available);
public sealed record CloudFile(string Id, string Title, string Kind, long Bytes, long QuotaBytes, long Created);
public sealed record CloudCourse(string Id, string Title, int Revision, long Updated);
public sealed record CloudJob(string Id, string? CourseId, string Kind, string State, string? Step, string? Error, long Created, string? CreditNotice);
public sealed record CloudSnapshot(CloudUser User, CloudStorage Storage, CloudWallet Wallet, CloudFile[] Files, CloudCourse[] Courses, CloudJob[] Jobs);
public sealed record CloudTransferProgress(long Bytes, long? Total);
public sealed class CloudApiException(string code, string message) : Exception(message) { public string Code { get; } = code; }

public sealed class CloudResources : IDisposable
{
    public const long MaximumFileBytes = 200L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CloudApiTransport _api;
    private readonly CloudAuthClient _authentication;
    private readonly string _directory;
    private readonly SemaphoreSlim _transfer = new(1, 1);
    private string? _user;

    public CloudResources(CloudAuthClient authentication, string dataDirectory, HttpClient? http = null)
    { _authentication = authentication; _api = new(authentication, http); _directory = Path.Combine(dataDirectory, "cloud-transfers"); }

    public async Task<CloudSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var user = await GetAsync<CloudUser>("api/v1/auth/me", ct);
        _user = user.Id;
        var storage = GetAsync<CloudStorage>("api/v1/storage", ct);
        var wallet = GetAsync<CloudWallet>("api/v1/wallet", ct);
        var files = GetAsync<CloudFile[]>("api/v1/library", ct);
        var courses = GetAsync<CloudCourse[]>("api/v1/courses", ct);
        var jobs = GetAsync<CloudJob[]>("api/v1/jobs", ct);
        await Task.WhenAll(storage, wallet, files, courses, jobs);
        return new(user, await storage, await wallet, await files, await courses, await jobs);
    }

    public async Task<CloudFile> UploadAsync(string source, IProgress<CloudTransferProgress>? progress, CancellationToken ct = default)
    {
        await _transfer.WaitAsync(ct);
        try
        {
            await EnsureUserAsync(ct);
            var info = new FileInfo(source);
            if (!info.Exists || info.Length == 0 || info.Length > MaximumFileBytes || Path.GetExtension(source).ToLowerInvariant() is not (".ilp" or ".zip"))
                throw new CloudApiException("invalid_package", "请选择不超过 200 MiB 的 ILP 或工程 ZIP。");
            await using var input = File.OpenRead(source);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)); input.Position = 0;
            var operation = await LoadOperationAsync("upload-" + hash, ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CloudAuthClient.Server, "api/v1/library/files"));
            request.Headers.Add("Idempotency-Key", operation.Key);
            using var form = new MultipartFormDataContent();
            form.Add(new ProgressContent(input, progress), "file", info.Name);
            request.Content = form;
            using var response = await _api.SendAsync(request, ct);
            await RequireSuccessAsync(response, ct);
            var result = await ReadAsync<CloudFile>(response, ct);
            File.Delete(operation.Path);
            return result;
        }
        finally { _transfer.Release(); }
    }

    public async Task<string> DownloadAsync(string resourceId, string kind, string format, IProgress<CloudTransferProgress>? progress,
        CancellationToken ct = default)
    {
        if (kind is not ("file" or "course") || format is not ("ilp" or "project")) throw new ArgumentException("云端文件类型无效。");
        await _transfer.WaitAsync(ct);
        TransferOperation? operation = null;
        string? file = null;
        try
        {
            await EnsureUserAsync(ct);
            operation = await LoadOperationAsync(DownloadKey(resourceId, kind, format), ct);
            file = operation.Path + "." + (format == "ilp" ? "ilp" : "zip");
            if (operation.Complete && File.Exists(file)) return file;
            if (operation.Id is null)
            {
                var endpoint = kind == "file" ? $"api/v1/library/{Uri.EscapeDataString(resourceId)}/downloads"
                    : $"api/v1/courses/{Uri.EscapeDataString(resourceId)}/downloads?format={format}";
                var offer = await PostAsync<DownloadOffer>(endpoint, operation.Key, ct);
                operation.Id = offer.Id;
                await SaveOperationAsync(operation, ct);
            }
            var start = kind == "file" ? $"api/v1/library/downloads/{Uri.EscapeDataString(operation.Id)}/start"
                : $"api/v1/deliveries/{Uri.EscapeDataString(operation.Id)}/start";
            var delivery = await PostAsync<DownloadStart>(start, null, ct);
            if (delivery.Bytes is <= 0 or > MaximumFileBytes || delivery.Sha256 is not { Length: 64 }
                || !delivery.Sha256.All(Uri.IsHexDigit)) throw new CloudApiException("invalid_response", "云端文件大小或完整性信息无效。");
            var target = new Uri(CloudAuthClient.Server, delivery.Url);
            var partial = file + ".part";
            var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await _api.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partial);
                throw new CloudApiException("range_retry", "本地下载片段已重置，请重试本次下载。");
            }
            await RequireSuccessAsync(response, ct);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange?.From != offset)
                    throw new CloudApiException("invalid_range", "云端返回的续传范围无效，请重试。");
            }
            else offset = 0;
            var total = delivery.Bytes;
            var declared = response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength is { } size ? size + offset : (long?)null);
            if (declared.HasValue && declared != total || offset > total)
                throw new CloudApiException("invalid_response", "云端文件的接收长度与授权不一致。");
            await using (var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None))
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[65536];
                var received = offset;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    received += read;
                    if (received > MaximumFileBytes) throw new CloudApiException("file_too_large", "云端文件超过 200 MiB，已停止下载。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report(new(received, total));
                }
                if (received != total) throw new IOException("云端文件接收不完整，请重试本次下载。");
            }
            string hash;
            await using (var verify = File.OpenRead(partial)) hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, ct));
            if (!hash.Equals(delivery.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new CloudApiException("hash_mismatch", "云端文件校验失败，请重试本次下载。");
            }
            File.Move(partial, file, true);
            operation.Complete = true;
            await SaveOperationAsync(operation, ct);
            return file;
        }
        catch (CloudApiException error) when (error.Code is "download" or "delivery")
        {
            if (operation is not null) File.Delete(operation.Path);
            if (file is not null) { File.Delete(file); File.Delete(file + ".part"); }
            throw new CloudApiException("authorization_expired", "下载授权已失效。再次点击将重新申请授权，可能扣除 0.2 积分。");
        }
        finally { _transfer.Release(); }
    }

    public async Task CompleteDownloadAsync(string resourceId, string kind, string format, CancellationToken ct = default)
    {
        await _transfer.WaitAsync(ct);
        try
        {
            var path = OperationPath(DownloadKey(resourceId, kind, format));
            File.Delete(path + "." + (format == "ilp" ? "ilp" : "zip"));
            File.Delete(path);
        }
        finally { _transfer.Release(); }
    }

    private async Task EnsureUserAsync(CancellationToken ct) => _user = (await GetAsync<CloudUser>("api/v1/auth/me", ct)).Id;
    private static string DownloadKey(string id, string kind, string format) => $"download:{kind}:{format}:{id}";
    private string OperationPath(string logicalKey)
    {
        if (_user is null) throw new CloudAuthenticationException("login_required", "请先登录 IL Cloud。");
        var device = logicalKey.StartsWith("download:", StringComparison.Ordinal) ? _authentication.DeviceId : "";
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_user + ":" + device + ":" + logicalKey)));
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, key + ".json");
    }
    private async Task<TransferOperation> LoadOperationAsync(string key, CancellationToken ct)
    {
        var path = OperationPath(key);
        var operation = File.Exists(path) ? JsonSerializer.Deserialize<TransferOperation>(await File.ReadAllTextAsync(path, ct), Json) : null;
        operation ??= new(); operation.Path = path;
        await SaveOperationAsync(operation, ct);
        return operation;
    }
    private static async Task SaveOperationAsync(TransferOperation operation, CancellationToken ct)
    {
        await File.WriteAllTextAsync(operation.Path + ".tmp", JsonSerializer.Serialize(operation, Json), ct);
        File.Move(operation.Path + ".tmp", operation.Path, true);
    }
    private async Task<T> GetAsync<T>(string endpoint, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(CloudAuthClient.Server, endpoint));
        using var response = await _api.SendAsync(request, deadline.Token);
        await RequireSuccessAsync(response, deadline.Token);
        return await ReadAsync<T>(response, deadline.Token);
    }
    private async Task<T> PostAsync<T>(string endpoint, string? key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CloudAuthClient.Server, endpoint));
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await _api.SendAsync(request, ct);
        await RequireSuccessAsync(response, ct);
        return await ReadAsync<T>(response, ct);
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new CloudApiException("invalid_response", "云服务返回的数据无效。");
    private static async Task RequireSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            if (body is not null) throw new CloudApiException(body.Error, body.Message);
        }
        catch (JsonException) { }
        throw new CloudApiException("http_error", $"云服务请求失败（HTTP {(int)response.StatusCode}）。");
    }
    public void Dispose() => _api.Dispose();
    private sealed record DownloadOffer(string Id, long Expires);
    private sealed record DownloadStart(string Url, string Sha256, long Bytes, string FileName, long Expires);
    private sealed record ErrorBody(string Error, string Message);
    private sealed class TransferOperation
    {
        public TransferOperation() { }
        public string Key { get; set; } = Guid.NewGuid().ToString("N");
        public string? Id { get; set; }
        public bool Complete { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string Path { get; set; } = "";
    }
    private sealed class ProgressContent(Stream source, IProgress<CloudTransferProgress>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = source.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => CopyAsync(target, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken ct) => CopyAsync(target, ct);
        private async Task CopyAsync(Stream target, CancellationToken ct)
        {
            var buffer = new byte[65536]; long sent = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct); if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                sent += read; progress?.Report(new(sent, source.Length));
            }
        }
    }
}
