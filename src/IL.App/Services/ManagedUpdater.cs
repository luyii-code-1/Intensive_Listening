using System.Diagnostics;
using System.Text.Json;
using System.Text;
using IL.Core.Management;
using IL.Core.Updates;
namespace IL.App.Services;

public sealed class ManagedUpdater : IDisposable
{
    private readonly Func<ManagedPolicy> _policy;
    private readonly Func<MdnConfiguration?> _configuration;
    private readonly Func<Uri, string?> _credential;
    private readonly Func<ManagedUpdate, CancellationToken, Task<string>> _download;
    private readonly string _data, _version;
    private readonly ManagedUpdateJournal _journal;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ManagedUpdate? _offered, _ready;
    private string? _file;
    private bool _busy, _automaticPending;
    private string Root => Path.Combine(_data, "updates");
    public string Status { get; private set; } = "尚无组织更新任务";
    public bool IsBusy => _busy || _automaticPending;
    public bool HasUpdate => _offered is not null && _offered == EligibleUpdate();
    public bool CanUpdate => HasUpdate && !IsBusy && !_journal.Attempted(_offered!.Id);
    public bool CanInstall => CanUpdate && _ready == _offered && _file is not null;
    public event Action? Changed;
    public event Action<ManagedUpdate>? NotificationRequested;
    public Func<Task>? AutomaticInstall { get; set; }
    private MdnClient? _client;
    public bool DeferredUntilClose { get; private set; }
    public bool ReopenAfterUpdate { get; set; } = true;
    public bool SkipUpdateOobe { get; set; }
    public string? UpdateMode => _offered?.Mode;
    public bool CanInstallDeferred => DeferredUntilClose && _ready is not null && _ready == EligibleUpdate();
    public ManagedUpdater(MdnClient client, string data) : this(() => client.Policy, () => client.Configuration, client.DownloadCredential, data, AppBuildInfo.Version) { _client = client; }
    public void DeferUntilClose() { DeferredUntilClose = true; ReopenAfterUpdate = false; _ = FeedbackAsync("deferred", "用户选择关闭窗口后更新"); }
    private Task FeedbackAsync(string status, string message = "", int progress = 0) => _offered is not null && _client is not null ? _client.ReportUpdateAsync(_offered.Id, status, message, progress) : Task.CompletedTask;
    internal ManagedUpdater(Func<ManagedPolicy> policy, Func<MdnConfiguration?> configuration, Func<Uri, string?> credential,
        string data, string version, Func<ManagedUpdate, CancellationToken, Task<string>>? download = null)
    {
        _policy = policy; _configuration = configuration; _credential = credential; _data = data; _version = version;
        _journal = new(Root); _download = download ?? DownloadAsync;
    }
    private ManagedUpdate? EligibleUpdate()
    {
        var update = _policy().Update;
        return update is not null && Guid.TryParse(update.Id, out _) && _configuration() is not null &&
            !_policy().DisabledFeatures.Contains("updates") && ManagedUpdateJournal.AllowsVersion(_version, update.Version) && !_journal.Completed(update.Id) ? update : null;
    }
    private ManagedUpdateDownloader Downloader(ManagedUpdate update)
    {
        var config = _configuration() ?? throw new InvalidOperationException("尚未加入组织管理。");
        return new(Root, config.UpdatePublicKey, config.ServerCertificateSha256, config.Server, _credential(new Uri(update.Url)));
    }
    private readonly SemaphoreSlim _progressGate = new(1, 1);
    private int _latestProgress;
    private async Task SendProgressAsync(int value)
    {
        _latestProgress = value;
        await _progressGate.WaitAsync();
        try { await FeedbackAsync("downloading", progress: _latestProgress); }
        finally { _progressGate.Release(); }
    }
    private Task<string> DownloadAsync(ManagedUpdate update, CancellationToken ct) => Downloader(update).DownloadAsync(update, ct, value => _ = SendProgressAsync(value));
    public void RecordResult(string id, string? version, bool success)
    {
        if (success && version == _version && Guid.TryParse(id, out _)) _journal.MarkCompleted(id, version);
    }
    public async Task RefreshAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        ManagedUpdate? automatic = null;
        try
        {
            var update = EligibleUpdate();
            if (update is null)
            {
                _offered = _ready = null; _file = null;
                var current = _policy().Update;
                Status = current is not null && Guid.TryParse(current.Id, out _) && _journal.Completed(current.Id) ? "当前组织更新任务已完成" : "尚无可执行的组织更新任务";
                Changed?.Invoke(); return;
            }
            var alreadyOffered = _offered == update;
            if (!alreadyOffered) { _offered = _ready = null; _file = null; DeferredUntilClose = false; }
            Downloader(update).Validate(update); _offered = update;
            if (update.Mode == "cold")
            {
                automatic = update;
            }
            else if (_journal.Attempted(update.Id))
                Status = $"组织更新 {update.Version} 已尝试，等待管理员重试指令";
            else if (!_automaticPending && AutomaticInstall is not null)
            { Status = $"您的组织正在更新您的应用至 {update.Version}"; _automaticPending = true; automatic = update; }
            Changed?.Invoke();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { _offered = _ready = null; _file = null; Status = "组织更新描述校验失败，请联系管理员"; Changed?.Invoke(); }
        finally { _gate.Release(); }
        if (automatic is null) return;
        if (automatic.Mode == "cold")
        {
            if (await PrepareAsync() && _journal.NoticeOnce(automatic.Id)) NotificationRequested?.Invoke(automatic);
            return;
        }
        try
        {
            if (automatic == EligibleUpdate() && await PrepareAsync() && automatic == EligibleUpdate() && _journal.AttemptOnce(automatic.Id)) await AutomaticInstall!();
        }
        finally { _automaticPending = false; Changed?.Invoke(); }
    }
    public async Task<bool> PrepareAsync()
    {
        if (!await _gate.WaitAsync(0)) return false;
        _busy = true; Changed?.Invoke();
        try
        {
            var update = EligibleUpdate();
            if (update is null || _offered != update) return false;
            if (_ready == update && _file is not null) return true;
            Status = "正在下载并校验组织更新…"; Changed?.Invoke();
            await FeedbackAsync("received"); await FeedbackAsync("downloading");
            var file = await _download(update, _stop.Token);
            await _progressGate.WaitAsync(_stop.Token); _progressGate.Release();
            if (update != EligibleUpdate()) { _ready = null; _file = null; Status = "组织更新任务已撤销或变更"; return false; }
            _ready = update; _file = file; Status = $"组织更新 {update.Version} 已校验，准备安装";
            await FeedbackAsync("ready", progress: 100);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is not OutOfMemoryException)
        { Status = "组织更新下载或校验失败，可点击更新重试"; await FeedbackAsync("failed", Status); return false; }
        finally { _busy = false; _gate.Release(); Changed?.Invoke(); }
    }
    public async Task LaunchInstallerAsync()
    {
        ManagedRuntime.Require("updates");
        if (_ready is null || _file is null || _ready != EligibleUpdate()) throw new InvalidOperationException("没有已校验的安装包。");
        _busy = true; Changed?.Invoke();
        try
        {
            await Downloader(_ready).VerifyAsync(_file, _ready, _stop.Token);
            var helper = Path.Combine(Root, "install-update.ps1");
            await File.WriteAllTextAsync(helper, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "tools", "install-update.ps1")), new UTF8Encoding(true), _stop.Token);
            var plan = Path.Combine(Root, "plan.json");
            File.WriteAllText(plan, JsonSerializer.Serialize(new { id = _ready.Id, version = _ready.Version, organization = _configuration()?.OrganizationName, installer = _file, sha256 = _ready.Sha256, size = _ready.Size, processId = Environment.ProcessId, startedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime(), executable = Environment.ProcessPath, appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), dataDirectory = _data, automaticOobe = SkipUpdateOobe, reopen = ReopenAfterUpdate, feedback = true }));
            await FeedbackAsync("installing");
            await StartHelperAsync(helper, plan, Path.Combine(Root, Guid.NewGuid().ToString("N") + ".ready"), _stop.Token);
            _journal.AttemptOnce(_ready.Id); Status = "正在安装组织更新，应用即将重新启动";
        }
        catch { _busy = false; Status = "更新助手启动失败，应用已保留；请查看 data/updates/install-update.log 并重试"; await FeedbackAsync("failed", "更新助手启动失败"); throw; }
        finally { Changed?.Invoke(); }
    }
    internal static async Task<int> ReportInstallerResultAsync(string data)
    {
        try
        {
            var result = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(data, "updates", "result.json")));
            var id = result.GetProperty("id").GetString()!;
            var version = result.TryGetProperty("version", out var value) ? value.GetString() : null;
            var success = result.GetProperty("success").GetBoolean() && version == AppBuildInfo.Version;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = new MdnClient(data, AppBuildInfo.Version);
            try { await client.SynchronizeAsync(timeout.Token); } catch (Exception e) when (e is not OutOfMemoryException) { }
            await client.ReportUpdateAsync(id, success ? "success" : "failed", success ? "已成功更新到 " + version : "安装未完成或用户取消 UAC", success ? 100 : 0);
            if (success) new ManagedUpdateJournal(Path.Combine(data, "updates")).MarkCompleted(id, version!);
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return 1; }
    }
    internal static async Task StartHelperAsync(string helper, string plan, string ready, CancellationToken ct, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper, "-Plan", plan, "-Ready", ready }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        var deadline = Stopwatch.StartNew();
        try
        {
            bool IsReady()
            {
                try { return File.Exists(ready) && File.ReadAllText(ready).Trim() == "ready"; }
                catch (IOException) { return false; }
            }
            while (!IsReady())
            {
                if (process.HasExited)
                {
                    await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(plan)!, "install-update.log"), $"{DateTimeOffset.Now:O} helper exit {process.ExitCode}\n{await errors}\n{await output}\n", ct);
                    throw new IOException("更新助手未能启动，应用已保留。请查看 data/updates/install-update.log。");
                }
                if (deadline.Elapsed >= (timeout ?? TimeSpan.FromSeconds(10))) throw new TimeoutException("更新助手启动超时，应用已保留。请重试更新。");
                await Task.Delay(50, ct);
            }
        }
        catch { if (!process.HasExited) process.Kill(); throw; }
        finally { if (File.Exists(ready)) File.Delete(ready); }
    }
    public void Dispose() => _stop.Cancel();
}
