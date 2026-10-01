using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IL.Core.Infrastructure;
namespace IL.Core.Telemetry;
public sealed class AppTelemetry(string version="2.0.0-dev",ITelemetryTransport? transport=null,string? dataDirectory=null,bool? supportedPlatform=null)
{
    private readonly ITelemetryTransport _transport=transport??new NativeTelemetryTransport(); private readonly SemaphoreSlim _gate=new(1,1); private readonly bool _supported=supportedPlatform??(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
    private volatile bool _consent,_enabled; private bool _launchReported,_activeReported; public bool Enabled=>_enabled;
    private string Root=>dataDirectory??AppDirectories.DataDirectory();
    public async Task ApplyConsentAsync(bool consent,bool mainInstance)
    {
        _consent=consent&&mainInstance&&_supported;if(!_consent)_enabled=false;await _gate.WaitAsync();
        try
        {
            if(!mainInstance||!_supported)return;var cache=Path.Combine(Root,"cache","arms-rum");
            if(!_consent){await _transport.StopAsync(cache);var pending=Path.Combine(Root,"telemetry","collect.json");if(File.Exists(pending))File.Delete(pending);return;}
            if(_enabled)return;Directory.CreateDirectory(cache);if(!_consent)return;_enabled=await Task.Run(()=>_transport.StartAsync(version,cache));if(!_enabled){AppLog.Warning("遥测组件未就绪，数据未上报");return;}if(!_consent){_enabled=false;await _transport.StopAsync(cache);return;}
            if(!_launchReported){var uuid=await _transport.InstallUuidAsync();if(CanSend){var fields=new Dictionary<string,string>{{"app_version",version}};if(!string.IsNullOrEmpty(uuid))fields["install_uuid"]=uuid;_launchReported=await _transport.EventAsync("app_launch",fields);}}
            var reachable=await _transport.CanReachCollectorAsync();await ReportActiveAsync(reachable);if(reachable)await ReportProfileAsync();
        }catch(Exception e){AppLog.Warning("遥测状态切换失败: "+e.Message,e);}finally{_gate.Release();}
    }
    public async Task ShutdownAsync()
    {
        _consent = _enabled = false; await _gate.WaitAsync();
        try { await Task.Run(() => _transport.StopAsync("")); } finally { _gate.Release(); }
    }
    public async Task<bool> ReportCrashAsync(CrashReport report)
    {
        await _gate.WaitAsync();
        try
        {
            if (!CanSend || !report.UploadAllowed || !await _transport.CanReachCollectorAsync() || !CanSend) return false;
            return await _transport.ErrorLogAsync(JsonSerializer.Serialize(new { kind = "crash_report", report.Id, report.OccurredAt, report.AppVersion, report.Platform, report.Architecture, report.ProcessId, report.Component, report.ExceptionType, report.Message, report.Details, report.ExitCode, report.Fatal }));
        }
        catch (Exception e) { AppLog.Warning("崩溃遥测暂未送达", e); return false; }
        finally { _gate.Release(); }
    }
    private bool CanSend=>_enabled&&_consent;
    private async Task ReportActiveAsync(bool reachable)
    {
        if(_activeReported||!CanSend)return;var path=Path.Combine(Root,"telemetry","collect.json");var key=$"{DateTime.Now:yyyy-MM-dd}|{version}";var pending=new List<string>();var submitted=new HashSet<string>();if(File.Exists(path)){try{var j=JsonNode.Parse(await File.ReadAllTextAsync(path))!;pending.AddRange(j["pending"]?.AsArray().Select(x=>x!.ToString())??[]);submitted.UnionWith(j["submitted"]?.AsArray().Select(x=>x!.ToString())??[]);}catch(JsonException){AppLog.Warning("启动遥测队列读取失败，将重新建立");}}if(!submitted.Contains(key)&&!pending.Contains(key))pending.Add(key);Directory.CreateDirectory(Path.GetDirectoryName(path)!);async Task Save()=>await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{pending,submitted}));await Save();if(!reachable||!CanSend)return;var uuid=await _transport.InstallUuidAsync();foreach(var entry in pending.ToArray()){if(!CanSend)return;var parts=entry.Split('|');if(parts.Length!=2)continue;var fields=new Dictionary<string,string>{{"active_date",parts[0]},{"app_version",parts[1]}};if(!string.IsNullOrEmpty(uuid))fields["install_uuid"]=uuid;if(!await _transport.EventAsync("app_active",fields))break;pending.Remove(entry);submitted.Add(entry);if(entry==key)_activeReported=true;await Save();}
    }
    private async Task ReportProfileAsync(){if(!CanSend)return;var cycle=await _transport.InstallCycleAsync();if(string.IsNullOrEmpty(cycle))return;var marker=Path.Combine(Root,"telemetry","system-profile-cycle.txt");if(File.Exists(marker)&&(await File.ReadAllTextAsync(marker)).Trim()==cycle)return;var fields=new Dictionary<string,string>(await _transport.SystemProfileAsync()){["app_version"]=version};if(!CanSend||!await _transport.EventAsync("system_profile",fields))return;Directory.CreateDirectory(Path.GetDirectoryName(marker)!);await File.WriteAllTextAsync(marker,cycle);}
    public async Task AsrCompletedAsync(string? cacheProfile,DateTimeOffset? startedAt,DateTimeOffset? finishedAt,bool cacheHit,bool completed=true)
    {
        if(!CanSend||!completed||startedAt==null||finishedAt==null)return;string model="",host="unknown";try{var p=JsonNode.Parse(cacheProfile??"")!;var local=p["provider"]?.ToString()=="local";model=local?Path.GetFileName((p["localModel"]?.ToString()??"").Replace('\\','/')):p["model"]?.ToString()??"";if(local)host="local";else{if(Uri.TryCreate(p["baseUrl"]?.ToString(),UriKind.Absolute,out var uri)||Uri.TryCreate(p["endpoint"]?.ToString(),UriKind.Absolute,out uri)){if(Regex.IsMatch(uri.Host,"^[A-Za-z0-9.-]{1,253}$"))host=uri.Host.ToLowerInvariant();}}}catch{model="unknown";}if(model.IndexOfAny(['/','\\',':'])>=0)model="unknown";await SendAsync("asr_completed",new Dictionary<string,string>{{"cache_hit",cacheHit.ToString().ToLowerInvariant()},{"model",model},{"api_host",host},{"duration_ms",Math.Max(0,(long)(finishedAt.Value-startedAt.Value).TotalMilliseconds).ToString()}});
    }
    private async Task SendAsync(string name,IReadOnlyDictionary<string,string> fields){try{if(CanSend)await _transport.EventAsync(name,fields);}catch(Exception e){AppLog.Warning("遥测事件提交失败: "+e.Message,e);}}
    public async Task ErrorNoticeAsync(string title,string message){try{if(CanSend)await _transport.ErrorLogAsync($"{title}: {message}");}catch(Exception e){AppLog.Warning("错误提示遥测提交失败: "+e.Message,e);}}
}
