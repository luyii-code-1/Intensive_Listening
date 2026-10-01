using IL.App.ViewModels;
using IL.Core.Asr;
using IL.Core.Ilp;
using IL.Core.Infrastructure;
using IL.Core.Mcp;
using IL.Core.Projects;
using IL.Core.Settings;
using IL.Core.Student;
using IL.Core.Telemetry;
using IL.Core.Transcription;

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
    public AppPrivateApiService Api {get;}
    public AppPrivateApiServer Server {get;}
    public AppTelemetry Telemetry {get;} = new();
    public Func<Task<bool>>? ConfirmForcedCuts {get;set;}
    public bool Standalone {get;}
    public AppServices(Action<Action> dispatch,bool standalone)
    {
        Standalone=standalone;Projects=new(Path.Combine(DataDirectory,"projects"));
        Student=new(()=>new LibVlcAudioPlayer(),LibraryDirectory,new LessonProgressStore(Path.Combine(DataDirectory,"lesson_progress.json")),SettingsStore,dispatch);
        Queue=new(RunAsrAsync,FindDuplicateAsync,new QueueStore(Path.Combine(DataDirectory,"transcription_queue.json")),new SrtRecognitionCache(Path.Combine(DataDirectory,"cache","asr-srt")),()=>Settings.AsrCacheProfile);
        Api=new(Projects,LibraryDirectory);Server=new(Api.DispatchAsync,Path.Combine(DataDirectory,"mcp","server.json"));
        Api.ExportStandalone=ExportStandaloneAsync;
        Api.StartAsr=async project=>
        {
            if(!Settings.CloudReady)throw new AsrException("请先在设置中完成云端 ASR 配置");
            if(project.TranscriptionJobId is {} old){Queue.Cancel(old);Queue.MarkSrtConsumed(old);}
            var job=Queue.Enqueue(project.Title,project.AudioPath!,project.AudioDuration,project.Id);var updated=project with{TranscriptionJobId=job,Step=CourseProjectStep.Transcription};await Projects.SaveAsync(updated);return updated;
        };
        Api.TranscriptReplacing=project=>{if(project.TranscriptionJobId is {} id){Queue.Cancel(id);Queue.MarkSrtConsumed(id);}};
        Queue.Completed+=(job,cacheHit)=>{_ = Telemetry.AsrCompletedAsync(job.CacheProfile,job.StartedAt,job.FinishedAt,cacheHit);};
    }
    public async Task InitializeAsync(){Settings=await SettingsStore.LoadAsync();AppLog.DebugEnabled=Settings.DebugLogging;await Queue.RestoreAsync();await Student.RefreshAsync();}
    public async Task ApplySettingsAsync(AppSettings settings)
    {
        Settings=settings;AppLog.DebugEnabled=settings.DebugLogging;
        CrashMonitor.SetConsent(settings.TelemetryEnabled && !Standalone);
        Student.TranscriptFontSize=settings.TranscriptFontSize;
        if(!Standalone){if(settings.FileAssociationPrompted)await new IlpFileAssociation().ApplyAsync(settings.FileAssociationEnabled);if(settings.McpEnabled)await Server.StartAsync();else await Server.StopAsync();}
        await Telemetry.ApplyConsentAsync(settings.TelemetryEnabled,!Standalone);
        if (!Standalone) await new CrashReportStore(DataDirectory).FlushAsync(settings.TelemetryEnabled, Telemetry.ReportCrashAsync);
    }
    private async Task<string> RunAsrAsync(string audio,IProgress<AsrProgress>? progress,CancellationToken ct,TimeSpan? estimated)
    {
        var settings=Settings;
        if(settings.AsrProvider==AsrProviderKind.Local)throw new AsrException("本地模型转写引擎尚未接入，请选择云端转写");
        if(!settings.CloudReady)throw new AsrException("请在设置中完成云端 ASR 配置");
        var config=new AsrConfig(settings.CloudBaseUrl,settings.CloudEndpoint,settings.CloudModel,settings.CloudApiKey,settings.CloudLanguage);
        return await new SegmentedAsrRunner(config,settings.CloudConcurrency,TimeSpan.FromSeconds(settings.CloudTimeoutSeconds)).RunAsync(audio,progress,ct,ConfirmForcedCuts);
    }
    private async Task<DuplicateMatch?> FindDuplicateAsync(string sha)
    {
        var lesson=await new IlpLibrary(LibraryDirectory).FindByAudioSha256Async(sha);
        return lesson is null?null:new(DuplicateCase.SharedAudioLesson,lesson.Manifest.Title,LessonId:lesson.Id);
    }
    public async Task<string> ExportStandaloneAsync(CourseProject project,string output)
    {
        if(!OperatingSystem.IsWindows())throw new InvalidOperationException("请在 Windows 运行版本中导出独立播放器");
        var temp=Path.Combine(Path.GetTempPath(),"il2-delivery-"+Guid.NewGuid().ToString("N")+".ilp");
        try{await new ProjectDelivery().CreateIlpAsync(project,temp);return await new StandaloneLessonExporter(AppContext.BaseDirectory,Path.Combine(AppContext.BaseDirectory,"tools","lesson_player_launcher.exe")).ExportAsync(temp,output);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public async ValueTask DisposeAsync(){await Server.DisposeAsync();await Queue.DisposeAsync();await Student.DisposeAsync();await Telemetry.ShutdownAsync();}
}
