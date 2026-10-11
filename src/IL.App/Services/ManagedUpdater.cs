using System.Diagnostics;
using System.Text.Json;
using IL.Core.Management;
using IL.Core.Updates;
namespace IL.App.Services;

public sealed class ManagedUpdater(MdnClient client, string data) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ManagedUpdate? _ready;
    private string? _file;
    public string Status { get; private set; } = "";
    public bool CanInstall => _ready is not null && _file is not null;
    public event Action? Changed;
    public Func<Task>? AutomaticInstall { get; set; }
    private string Root => Path.Combine(data, "updates");
    public async Task RefreshAsync()
    {
        var update = client.Policy.Update;
        if(update is null || _ready?.Id!=update.Id){_ready=null;_file=null;}
        if (update is null || client.Configuration is null || !ManagedRuntime.Allows("updates") || !Version.TryParse(update.Version, out var next) || next <= new Version(AppBuildInfo.Version)) return;
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var attempt = Path.Combine(Root, update.Id + ".attempt");
            if (_ready?.Id == update.Id) return;
            Status = "正在下载并校验组织更新…"; Changed?.Invoke();
            var download = new ManagedUpdateDownloader(Root, client.Configuration.UpdatePublicKey,client.Configuration.ServerCertificateSha256);
            var file = await download.DownloadAsync(update, _stop.Token);
            if(update.Id!=client.Policy.Update?.Id||!ManagedRuntime.Allows("updates"))return;
            _file=file; _ready = update;
            Status = $"组织更新 {update.Version} 已校验，可安装"; Changed?.Invoke();
            if (update.Mode == "ota" && !File.Exists(attempt) && AutomaticInstall is not null) await AutomaticInstall();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Status = "组织更新下载或校验失败"; Changed?.Invoke(); }
        finally { _gate.Release(); }
    }
    public async Task LaunchInstallerAsync()
    {
        ManagedRuntime.Require("updates");
        if (_ready is null || _file is null || _ready.Id != client.Policy.Update?.Id) throw new InvalidOperationException("没有已校验的安装包。");
        await new ManagedUpdateDownloader(Root, client.Configuration!.UpdatePublicKey,client.Configuration.ServerCertificateSha256).VerifyAsync(_file, _ready, _stop.Token);
        var helper = Path.Combine(Root, "install-update.ps1");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "tools", "install-update.ps1"), helper, true);
        var plan = Path.Combine(Root, "plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new { id = _ready.Id, version = _ready.Version, installer = _file, sha256 = _ready.Sha256, size = _ready.Size, processId = Environment.ProcessId, startedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime(), executable = Environment.ProcessPath, appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), dataDirectory = data, automaticOobe = client.Policy.AutomaticOobe }));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper, "-Plan", plan }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
        File.WriteAllText(Path.Combine(Root, _ready.Id + ".attempt"), "started");
    }
    public void Dispose() => _stop.Cancel();
}
