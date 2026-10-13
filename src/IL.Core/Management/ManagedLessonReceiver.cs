using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IL.Core.Ilp;

namespace IL.Core.Management;

public sealed class ManagedLessonReceiver : IDisposable
{
    private readonly Func<ManagedPolicy> _policy;
    private readonly Func<MdnConfiguration?> _configuration;
    private readonly Func<Uri, string?> _credential;
    private readonly Func<string, string, string, CancellationToken, Task<bool>> _receipt;
    private readonly Func<ManagedLesson, CancellationToken, Task<string>>? _download;
    private readonly string _library, _data;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    public string Status { get; private set; } = "尚无组织精听包任务";
    public event Action? Changed;
    public event Action<ManagedLesson>? Imported;
    public Func<string, Task<bool>> IsInUse { get; set; } = _ => Task.FromResult(false);
    public Func<Task> LibraryChanged { get; set; } = () => Task.CompletedTask;
    public ManagedLessonReceiver(MdnClient client, string library, string data) : this(() => client.Policy, () => client.Configuration, client.DownloadCredential, client.ReportLessonAsync, library, data) { }
    internal ManagedLessonReceiver(Func<ManagedPolicy> policy, Func<MdnConfiguration?> configuration, Func<Uri, string?> credential,
        Func<string, string, string, CancellationToken, Task<bool>> receipt, string library, string data, Func<ManagedLesson, CancellationToken, Task<string>>? download = null)
    { _policy = policy; _configuration = configuration; _credential = credential; _receipt = receipt; _library = library; _data = data; _download = download; }
    private sealed record Receipt(string Status, string Message, bool Reported = false);
    public async Task RefreshAsync()
    {
        if (_stop.IsCancellationRequested || !await _gate.WaitAsync(0)) return;
        try
        {
            var configuration = _configuration();
            if (configuration is null) { Set("尚未加入组织管理"); return; }
            var context = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration.Server.AbsoluteUri + configuration.UpdatePublicKey)));
            var root = Path.Combine(_data, "mdn", "lessons", context); Directory.CreateDirectory(root);
            var pending = _policy().Lessons.OrderByDescending(l => l.PackageVersion).ToArray();
            if (pending.Length == 0) { Set("尚无组织精听包任务"); return; }
            int deferred = 0, failed = 0, completed = 0;
            foreach (var item in pending)
            {
                if (_stop.IsCancellationRequested || _configuration() != configuration) break;
                var receiptPath = Path.Combine(root, Guid.Parse(item.Id).ToString("D") + ".json");
                var downloader = new ManagedLessonDownloader(root, configuration, _credential(new Uri(item.Url)));
                async Task ReportAsync(string state, string message)
                {
                    var result = new Receipt(state, message);
                    await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(result), _stop.Token);
                    try { if (await _receipt(item.Id, state, message, _stop.Token)) await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(result with { Reported = true }), _stop.Token); }
                    catch (Exception e) when (e is not OutOfMemoryException && e is not OperationCanceledException) { }
                }
                try
                {
                    downloader.Validate(item);
                    Receipt? saved = null;
                    if (File.Exists(receiptPath))
                    { try { saved = JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(receiptPath, _stop.Token)); } catch (JsonException) { } }
                    if (saved?.Status is "imported" or "skipped")
                    { if (!saved.Reported) await ReportAsync(saved.Status, saved.Message); completed++; continue; }
                    var existing = await new IlpLibrary(_library).FindByUuidAsync(item.PackageUuid, _stop.Token);
                    if (existing?.Manifest.PackageVersion >= item.PackageVersion)
                    { await ReportAsync("skipped", "本地已有相同或更高版本"); completed++; continue; }
                    if (await IsInUse(item.PackageUuid))
                    { deferred++; Set($"{item.Title}：等待当前课程退出后更新"); await ReportAsync("deferred", "正在使用课程，退出后自动更新"); continue; }
                    Set($"正在下载组织精听包：{item.Title}"); await ReportAsync("downloading", "正在下载并校验");
                    var file = _download is null ? await downloader.DownloadAsync(item, _stop.Token) : await _download(item, _stop.Token);
                    await downloader.VerifyAsync(file, item, _stop.Token);
                    if (_configuration() != configuration || !_policy().Lessons.Contains(item)) continue;
                    var importer = new IlpImporter(_library);
                    var manifest = await Task.Run(() => importer.ReadManifestAsync(file, _stop.Token), _stop.Token);
                    if (!string.Equals(manifest.PackageUuid, item.PackageUuid, StringComparison.OrdinalIgnoreCase) || manifest.PackageVersion != item.PackageVersion) throw new InvalidOperationException("精听包清单与下发描述不一致。");
                    if (await IsInUse(item.PackageUuid))
                    { deferred++; Set($"{item.Title}：等待当前课程退出后更新"); await ReportAsync("deferred", "正在使用课程，退出后自动更新"); continue; }
                    existing = await new IlpLibrary(_library).FindByUuidAsync(item.PackageUuid, _stop.Token);
                    if (existing?.Manifest.PackageVersion >= item.PackageVersion) { await ReportAsync("skipped", "本地已有相同或更高版本"); completed++; continue; }
                    if (_configuration() != configuration || !_policy().Lessons.Contains(item)) continue;
                    await Task.Run(() => importer.ImportFileAsync(file, replaceExisting: existing is not null, ct: _stop.Token), _stop.Token);
                    await ReportAsync("imported", existing is null ? "课程已导入" : "课程已更新，学习记录保留");
                    await LibraryChanged(); Imported?.Invoke(item); File.Delete(file);
                    completed++;
                    Set($"组织精听包已导入：{item.Title}");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception e) when (e is not OutOfMemoryException)
                { failed++; Set($"{item.Title}：接收失败，将自动重试"); await ReportAsync("failed", "下载、校验或导入失败，请检查客户端状态"); }
            }
            if (failed > 0) Set($"{failed} 个精听包接收失败，将自动重试");
            else if (deferred > 0) Set($"{deferred} 个精听包等待当前课程或制作工程退出后更新");
            else if (completed > 0) Set($"组织精听包已接收（{completed} 个任务）");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) when (e is not OutOfMemoryException) { Set("组织精听包接收未完成，将自动重试"); }
        finally { _gate.Release(); }
    }
    private void Set(string status) { if (Status == status) return; Status = status; Changed?.Invoke(); }
    public void Dispose() => _stop.Cancel();
}
