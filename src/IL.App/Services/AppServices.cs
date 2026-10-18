using IL.App.ViewModels;
using IL.Core.Asr;
using IL.Core.Management;
using IL.Core.Updates;
using IL.Core.Ilp;
using IL.Core.Infrastructure;
using IL.Core.Mcp;
using IL.Core.Projects;
using IL.Core.Settings;
using IL.Core.Student;
using IL.Core.Telemetry;
using IL.Core.Transcription;
using IL.Core.Cloud;

namespace IL.App.Services;

public sealed class AppServices : IAsyncDisposable
{
    public string DataDirectory {get;} = AppDirectories.DataDirectory();
    public string LibraryDirectory => Path.Combine(DataDirectory,"library");
    public AppSettingsStore SettingsStore {get;} = new();
    public AppSettings Settings {get;private set;} = AppSettings.Defaults();
    public CourseProjectStore Projects {get;}
    public StudentViewModel Student {get;}
    public TranscriptionQueue Queue {get;}
    public ProjectTranscriptionBinding Transcriptions {get;}
    public AppPrivateApiService Api {get;}
    public AppPrivateApiServer Server {get;}
    public AppTelemetry Telemetry {get;} = new();
    public MdnClient Mdn { get; }
    public GitHubUpdateChecker Updates { get; } = new();
    public ManagedUpdater ManagedUpdates { get; }
    public ManagedLessonReceiver ManagedLessons { get; }
    public CloudAccountService CloudAccount { get; } = new();
    public CloudResources CloudFiles { get; }
    public CloudAsrClient CloudAsr { get; }
    public Func<Task<bool>>? ConfirmForcedCuts {get;set;}
    public bool Standalone {get;}
    public string? PreconfigurationError { get; private set; }
    public AppServices(Action<Action> dispatch,bool standalone)
    {
        Standalone=standalone; Mdn=new(DataDirectory,AppBuildInfo.Version); if(!standalone)ManagedRuntime.Client=Mdn; ManagedUpdates=new(Mdn,DataDirectory) { Enabled = OperatingSystem.IsWindows() }; Projects=new(Path.Combine(DataDirectory,"projects"));
        CloudFiles = new(CloudAccount.Authentication, DataDirectory); CloudAsr = new(CloudAccount.Authentication, DataDirectory);
        Student=new(()=>new LibVlcAudioPlayer(),LibraryDirectory,new LessonProgressStore(Path.Combine(DataDirectory,"lesson_progress.json")),SettingsStore,dispatch);
        ManagedLessons = new(Mdn, LibraryDirectory, DataDirectory);
        Queue=new(RunAsrAsync,FindDuplicateAsync,new QueueStore(Path.Combine(DataDirectory,"transcription_queue.json")),new SrtRecognitionCache(Path.Combine(DataDirectory,"cache","asr-srt")),()=>Settings.AsrCacheProfile);
        Queue.RunJob = RunAsrJobAsync;
        Transcriptions=new(Projects,Queue);
        Mdn.Changed += ApplyStartup;
        Api=new(Projects,LibraryDirectory);Server=new(Api.DispatchAsync,Path.Combine(DataDirectory,"mcp","server.json"));
        Api.AgentName = () => Server.ActiveAgentName;
        Api.ExportStandalone=ExportStandaloneAsync;
        Api.StartAsr=async project=>
        {
            ManagedRuntime.Require("asr"); if(Settings.AsrServiceMode == AsrServiceMode.Api && !Settings.CloudReady)throw new AsrException("请先在设置中完成云端 ASR 配置");
            if(project.TranscriptionJobId is {} old){Queue.Cancel(old);Queue.MarkSrtConsumed(old);}
            return await Transcriptions.EnqueueAsync(project);
        };
        Api.TranscriptReplacing=project=>{if(project.TranscriptionJobId is {} id){Queue.Cancel(id);Queue.MarkSrtConsumed(id);}};
        Queue.Completed+=(job,cacheHit)=>{_ = Telemetry.AsrCompletedAsync(job.CacheProfile,job.StartedAt,job.FinishedAt,cacheHit);};
    }
    public async Task InitializeAsync()
    {
        if (!Standalone)
        {
            var hasSettings = SettingsStore.HasPersonalSettings;
            InstallationOnboarding.Prepare(DataDirectory, hasSettings);
            if (!hasSettings) await SettingsStore.SaveAsync(AppSettings.Defaults() with { FileAssociationEnabled = true, McpEnabled = true });
            try { await DeploymentPreset.ApplyAsync(SettingsStore, force: !hasSettings); }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
            { PreconfigurationError = "预设配置未应用，请检查 il_precfg.yaml 的格式和字段。"; AppLog.Warning(PreconfigurationError); }
        }
        Settings=await SettingsStore.LoadAsync(); _ = CloudAccount.ApplyEnabledAsync(Settings.CloudServiceEnabled); AppLog.DebugEnabled=Settings.DebugLogging;ApplyStartup();await Queue.RestoreAsync();await Transcriptions.RestorePendingAsync();await Student.RefreshAsync();
    }
    public async Task ApplySettingsAsync(AppSettings settings)
    {
        settings=await SettingsStore.NormalizeAsync(settings); Settings=settings;AppLog.DebugEnabled=settings.DebugLogging;
        ApplyStartup();
        await CloudAccount.ApplyEnabledAsync(settings.CloudServiceEnabled);
        CrashMonitor.SetConsent(settings.TelemetryEnabled && !Standalone);
        Student.TranscriptFontSize=settings.TranscriptFontSize;
        Student.TranscriptBlurIntensity=settings.TranscriptBlurIntensity;
        if(!ManagedRuntime.Allows("asr") || !settings.CloudServiceEnabled)foreach(var job in Queue.Jobs.Where(j=>j.IsActive && (!ManagedRuntime.Allows("asr") || j.CacheProfile?.Contains("il-cloud-service-v1", StringComparison.Ordinal) == true)))Queue.Cancel(job.Id);
        if(!Standalone){if(settings.FileAssociationPrompted || ManagedRuntime.Policy.Settings.ContainsKey("fileAssociationEnabled"))await new IlpFileAssociation().ApplyAsync(settings.FileAssociationEnabled);if(settings.McpEnabled)await Server.StartAsync();else await Server.StopAsync();}
        await Telemetry.ApplyConsentAsync(settings.TelemetryEnabled,!Standalone);
        if (!Standalone) await new CrashReportStore(DataDirectory).FlushAsync(settings.TelemetryEnabled, Telemetry.ReportCrashAsync);
    }
    private void ApplyStartup()
    {
        if (Standalone) return;
        try { WindowsStartup.Apply(Mdn.Configuration != null || Settings.StartAtLogin); }
        catch (Exception error) { AppLog.Error("设置开机自启失败", error); }
    }
    private async Task<string> RunAsrJobAsync(string id, string audio, IProgress<AsrProgress>? progress, CancellationToken ct, TimeSpan? estimated)
    {
        ManagedRuntime.Require("asr");
        if (Settings.AsrServiceMode != AsrServiceMode.CloudService) return await RunAsrAsync(audio, progress, ct, estimated);
        if (!CloudAccount.IsSignedIn) throw new AsrException("请先启用并登录 Intensive Listening 云服务。");
        var started = System.Diagnostics.Stopwatch.StartNew();
        try { var result = await CloudAsr.RunAsync(id, audio, progress, ct, () => Queue.WasUserCancelled(id), credits => Queue.SetChargedCredits(id, credits)); Mdn.Report("asr", new { durationMs = started.ElapsedMilliseconds, success = true, provider = "ilCloud" }); return result; }
        catch (CloudAsrCompletedAfterCancellationException) { Mdn.Report("asr", new { durationMs = started.ElapsedMilliseconds, success = true, provider = "ilCloud" }); throw; }
        catch (CloudAuthenticationException error) { CloudAccount.AuthenticationFailed(error.Message); throw new AsrException(error.Message, error); }
        catch (CloudApiException error) { throw new AsrException(error.Message, error); }
        catch (Exception error) { Mdn.Report("asr", new { durationMs = started.ElapsedMilliseconds, success = false, provider = "ilCloud", errorType = error.GetType().Name }); throw; }
    }
    private async Task<string> RunAsrAsync(string audio,IProgress<AsrProgress>? progress,CancellationToken ct,TimeSpan? estimated)
    {
        ManagedRuntime.Require("asr");
        var settings=Settings;
        if(settings.AsrProvider==AsrProviderKind.Local)throw new AsrException("本地模型转写引擎尚未接入，请选择云端转写");
        if(!settings.CloudReady)throw new AsrException("请在设置中完成云端 ASR 配置");
        var config=new AsrConfig(settings.CloudBaseUrl,settings.CloudEndpoint,settings.CloudModel,settings.CloudApiKey,settings.CloudLanguage);
        var started=System.Diagnostics.Stopwatch.StartNew();
        try { var result=await new SegmentedAsrRunner(config,settings.CloudConcurrency,TimeSpan.FromSeconds(settings.CloudTimeoutSeconds)).RunAsync(audio,progress,ct,ConfirmForcedCuts); Mdn.Report("asr",new {durationMs=started.ElapsedMilliseconds, success=true}); return result; }
        catch(Exception e) { Mdn.Report("asr",new {durationMs=started.ElapsedMilliseconds, success=false, errorType=e.GetType().Name}); throw; }
    }
    private async Task<DuplicateMatch?> FindDuplicateAsync(string sha)
    {
        var lesson=await new IlpLibrary(LibraryDirectory).FindByAudioSha256Async(sha);
        return lesson is null?null:new(DuplicateCase.SharedAudioLesson,lesson.Manifest.Title,LessonId:lesson.Id);
    }
    public async Task<string> ExportStandaloneAsync(CourseProject project,string output)
    {
        ManagedRuntime.Require("export"); if(!OperatingSystem.IsWindows())throw new InvalidOperationException("请在 Windows 运行版本中导出独立播放器");
        var temp=Path.Combine(Path.GetTempPath(),"il2-delivery-"+Guid.NewGuid().ToString("N")+".ilp");
        try{await new ProjectDelivery().CreateIlpAsync(project,temp);return await new StandaloneLessonExporter(AppContext.BaseDirectory,Path.Combine(AppContext.BaseDirectory,"tools","lesson_player_launcher.exe")).ExportAsync(temp,output);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public async ValueTask DisposeAsync()
    {
        try { ManagedLessons.Dispose();ManagedUpdates.Dispose();Updates.Dispose();await Mdn.DisposeAsync();await Server.DisposeAsync();await Queue.DisposeAsync();CloudAsr.Dispose();CloudFiles.Dispose();CloudAccount.Dispose();await Transcriptions.DisposeAsync();await Student.DisposeAsync();await Telemetry.ShutdownAsync(); }
        finally { Mdn.Changed -= ApplyStartup; AppLog.DebugEnabled = false; await RuntimeDiagnostics.Completion; }
    }
}
