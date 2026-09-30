using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using IL.App.Services;
using IL.App.Views;
using IL.App.Views.Dialogs;
using IL.Core.Infrastructure;
using IL.Core.Settings;

namespace IL.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly StudentView _student;
    private readonly TeacherView _teacher;
    private readonly QueueView _queue;
    private readonly SettingsView _settings;
    private bool _closing;
    public MainWindow()
    {
        InitializeComponent();
        var standalone=Program.Arguments.Contains("--standalone");
        _services=new(action=>Dispatcher.UIThread.Post(action),standalone);
        _student=new(_services.Student);
        _teacher=new(_services.Projects,_services.Queue,()=>_services.Settings,_services.LibraryDirectory,()=>Dispatcher.UIThread.Post(async()=>await _services.Student.RefreshAsync()),()=>ShowSettings());
        _settings=new(_services.SettingsStore,async settings=>{ApplyTheme(settings);await _services.ApplySettingsAsync(settings);});
        _queue=new(_services.Queue,async job=>{await _teacher.LoadJobTranscriptAsync(job);PageHost.Content=_teacher;},async match=>{if(match.LessonId is {} id){PageHost.Content=_student;_services.Student.SelectedLesson=_services.Student.Lessons.FirstOrDefault(l=>l.Id==id);await _services.Student.CurrentLoad;}},()=>PickAudioForQueueAsync());
        _services.ConfirmForcedCuts=()=>OnUiAsync(()=>AppDialogs.ConfirmForcedCutsAsync(this));
        _services.Server.ApprovalRequested=name=>OnUiAsync(async()=>{var decision=await AppDialogs.ApproveAgentAsync(this,name);if(decision==IL.Core.Mcp.AgentApprovalDecision.Approve)await _teacher.PrepareAgentSessionAsync();else if(decision==IL.Core.Mcp.AgentApprovalDecision.DisableMcp){var settings=_services.Settings with{McpEnabled=false};await _services.SettingsStore.SaveAsync(settings);await _services.ApplySettingsAsync(settings);}return decision;});
        _services.Server.AgentStateChanged+=active=>Dispatcher.UIThread.Post(async()=>{AgentButton.IsVisible=active;_teacher.SetAgentMode(active);if(active)PageHost.Content=_teacher;else await _teacher.FinishAgentSessionAsync();});
        _services.Api.ProjectChanged=()=>Dispatcher.UIThread.Post(async()=>await _teacher.RefreshAsync());
        _services.Api.OpenProject=id=>Dispatcher.UIThread.Post(async()=>{await _teacher.OpenProjectAsync(id);PageHost.Content=_teacher;});
        _services.Api.LibraryChanged=()=>Dispatcher.UIThread.Post(async()=>await _services.Student.RefreshAsync());
        _teacher.StandaloneExporter=async project=>{if(await WorkspaceUi.Save(this,"导出独立播放器",project.Title+".exe","exe") is {} file)return await _services.ExportStandaloneAsync(project,file);return null;};
        StudentButton.Click+=(_,_)=>PageHost.Content=_student;
        TeacherButton.Click+=async(_,_)=>{await _services.Student.PauseAsync();PageHost.Content=_teacher;await _teacher.RefreshAsync();};
        QueueButton.Click+=(_,_)=>PageHost.Content=_queue;SettingsButton.Click+=(_,_)=>ShowSettings();
        AgentButton.Click+=(_,_)=>_services.Server.DisconnectAgent();
        PageHost.Content=_student;
        if(standalone){TeacherButton.IsVisible=QueueButton.IsVisible=SettingsButton.IsVisible=false;Width=1120;}
        Opened+=async(_,_)=>await InitializeAsync();
        Closing+=async(_,e)=>{if(_closing)return;e.Cancel=true;_closing=true;try{await _services.DisposeAsync();}catch(Exception ex){AppLog.Warning("关闭时保存失败",ex);}Close();};
    }
    private void ShowSettings(){PageHost.Content=_settings;_settings.Populate(_services.Settings);}
    private static void ApplyTheme(AppSettings settings){if(Avalonia.Application.Current is {} app)app.RequestedThemeVariant=settings.ThemeMode switch{"dark"=>ThemeVariant.Dark,"light"=>ThemeVariant.Light,_=>ThemeVariant.Default};}
    private async Task InitializeAsync()
    {
        try
        {
            await _services.InitializeAsync();ApplyTheme(_services.Settings);await _teacher.RefreshAsync();_settings.Populate(_services.Settings);
            if(!_services.Standalone&&_services.Settings.EulaAcceptedVersion.Length==0)
            {
                var legal=Path.Combine(AppContext.BaseDirectory,"assets","legal");
                var settings=await AppDialogs.FirstRunAsync(this,_services.Settings,await File.ReadAllTextAsync(Path.Combine(legal,"eula_zh_cn.txt")),await File.ReadAllTextAsync(Path.Combine(legal,"privacy_zh_cn.txt")));
                if(settings is null){Close();return;}await _services.SettingsStore.SaveAsync(settings);_settings.Populate(settings);ApplyTheme(settings);await _services.ApplySettingsAsync(settings);
            }
            else await _services.ApplySettingsAsync(_services.Settings);
            for(var i=0;i<Program.Arguments.Length;i++)
            {
                var file=Program.Arguments[i];if(file.EndsWith(".ilp",StringComparison.OrdinalIgnoreCase)&&File.Exists(file)){await _student.ImportFileAsync(file,_services.Standalone);break;}
            }
            await AppLog.WriteAsync("INFO","应用初始化完成");
            if(_services.Queue.InterruptedCount>0&&await AppDialogs.ConfirmAsync(this,"恢复转写任务",$"发现 {_services.Queue.InterruptedCount} 个未完成的任务，继续处理？","恢复"))_services.Queue.ResumeInterrupted();
        }
        catch(Exception ex){AppLog.Warning("初始化失败",ex);_services.Student.Status=$"初始化失败：{ex.Message}";}
    }
    private async Task PickAudioForQueueAsync()
    {
        PageHost.Content=_teacher;await _teacher.PickAudioAndEnqueueAsync();
    }
    private static Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var completion=new TaskCompletionSource<T>();Dispatcher.UIThread.Post(async()=>{try{completion.SetResult(await action());}catch(Exception ex){completion.SetException(ex);}});return completion.Task;
    }
}
