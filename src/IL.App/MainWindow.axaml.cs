using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
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
    private bool _closing, _paneExpanded = true, _teacherLoaded;
    private bool _openingTeacher;
    private int _destination, _settingsBack;
    private string? _openedAudioPath;
    private FAContentDialog? _taskDialog;
    public MainWindow()
    {
        InitializeComponent();
        var standalone=Program.Arguments.Contains("--standalone");
        _services=new(action=>Dispatcher.UIThread.Post(action),standalone);
        _student=new(_services.Student);
        _teacher=new(_services.Projects,_services.Queue,()=>_services.Settings,_services.LibraryDirectory,()=>Dispatcher.UIThread.Post(async()=>await _services.Student.RefreshAsync()),()=>ShowSettings());
        _settings=new(_services.SettingsStore,async settings=>{ApplyTheme(settings);await _services.ApplySettingsAsync(settings);});
        _queue=new(_services.Queue,async job=>{_taskDialog?.Hide();await _teacher.LoadJobTranscriptAsync(job);Navigate(1);},async match=>{if(match.LessonId is {} id){_taskDialog?.Hide();Navigate(0);_services.Student.SelectedLesson=_services.Student.Lessons.FirstOrDefault(l=>l.Id==id);await _services.Student.CurrentLoad;}},()=>PickAudioForQueueAsync());
        _services.ConfirmForcedCuts=()=>OnUiAsync(()=>AppDialogs.ConfirmForcedCutsAsync(this));
        _services.Server.ApprovalRequested=name=>OnUiAsync(async()=>{var decision=await AppDialogs.ApproveAgentAsync(this,name);if(decision==IL.Core.Mcp.AgentApprovalDecision.Approve)await _teacher.PrepareAgentSessionAsync();else if(decision==IL.Core.Mcp.AgentApprovalDecision.DisableMcp){var settings=_services.Settings with{McpEnabled=false};await _services.SettingsStore.SaveAsync(settings);await _services.ApplySettingsAsync(settings);}return decision;});
        _services.Server.AgentStateChanged+=active=>Dispatcher.UIThread.Post(async()=>{AgentOverlay.IsVisible=active;ShellGrid.Effect=active?new BlurEffect{Radius=12}:null;_teacher.SetAgentMode(active);if(active)Navigate(1);else await _teacher.FinishAgentSessionAsync();});
        _services.Api.ProjectChanged=()=>Dispatcher.UIThread.Post(async()=>await _teacher.RefreshAsync());
        _services.Api.OpenProject=id=>Dispatcher.UIThread.Post(async()=>{await _teacher.OpenProjectAsync(id);Navigate(1);});
        _services.Api.LibraryChanged=()=>Dispatcher.UIThread.Post(async()=>await _services.Student.RefreshAsync());
        _teacher.StandaloneExporter=async project=>{if(await WorkspaceUi.Save(this,"导出独立播放器",project.Title+".exe","exe") is {} file)return await _services.ExportStandaloneAsync(project,file);return null;};
        StudentButton.Click+=(_,_)=>Navigate(0);
        TeacherButton.Click+=async(_,_)=>await ShowTeacherAsync();
        QueueButton.Click+=async(_,_)=>await ShowTasksAsync();SettingsButton.Click+=(_,_)=>ShowSettings();
        AgentButton.Click+=(_,_)=>_services.Server.DisconnectAgent();
        PaneToggle.Content=WorkspaceUi.Icon("global_nav_button");
        PaneToggle.Click+=(_,_)=>{_paneExpanded=!_paneExpanded;UpdatePane();};
        _settings.BackRequested=()=>Navigate(_settingsBack);
        _teacher.ProjectOpened=CollapsePane;
        _services.Student.PresentationChanged+=()=>{var path=_services.Student.AudioPath;if(path!=_openedAudioPath){_openedAudioPath=path;if(path!=null)CollapsePane();}};
        _settings.WithdrawAgreementRequested=async()=>
        {
            var reset=_services.Settings with{EulaAcceptedVersion="",TelemetryEnabled=false,TelemetryPrompted=false};
            await _services.SettingsStore.SaveAsync(reset);await _services.ApplySettingsAsync(reset);
            var marker=Path.Combine(AppDirectories.DataDirectory(),"installation","setup-cycle.txt");if(File.Exists(marker))File.Delete(marker);
            Close();
        };
        SizeChanged+=(_,_)=>UpdatePane();
        _services.Queue.Changed+=(_,_)=>Dispatcher.UIThread.Post(UpdatePane);
        Navigate(0);
        if(standalone){NavigationPane.IsVisible=false;ShellGrid.ColumnDefinitions[0].Width=new GridLength(0);Width=1120;}
        Opened+=async(_,_)=>await InitializeAsync();
        Closing+=async(_,e)=>{if(_closing)return;e.Cancel=true;_closing=true;try{await _services.DisposeAsync();}catch(Exception ex){AppLog.Warning("关闭时保存失败",ex);}Close();};
    }
    private void ShowSettings(){if(_destination!=2)_settingsBack=_destination;_settings.Populate(_services.Settings);Navigate(2);}
    private void Navigate(int destination)
    {
        _destination=destination;PageHost.Content=destination switch{1=>_teacher,2=>_settings,_=>_student};
        UpdatePane();
    }
    private async Task ShowTeacherAsync()
    {
        if (_openingTeacher || _destination == 1) return;
        var fromStudent = _destination == 0;
        _openingTeacher = true; NavigationBusy.IsVisible = true; _teacher.IsHitTestVisible = false;
        Navigate(1);
        try
        {
            // Present the cached page before waiting for media and disk operations.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await _services.Student.PauseAsync();
            if (fromStudent) await _teacher.ReturnToProjectsAsync();
            if (!_teacherLoaded) { await _teacher.RefreshAsync(); _teacherLoaded = true; }
        }
        catch (Exception ex) { AppLog.Warning("打开制作页面失败", ex); WorkspaceToast.Show(this, "无法打开制作页面", ex.Message, true); }
        finally { _openingTeacher = false; NavigationBusy.IsVisible = false; _teacher.IsHitTestVisible = true; }
    }
    private void CollapsePane(){_paneExpanded=false;UpdatePane();}
    private void UpdatePane()
    {
        if(_services.Standalone)return;
        var expanded=_paneExpanded&&ClientSize.Width>=1200;
        ShellGrid.ColumnDefinitions[0].Width=new GridLength(expanded?320:48);
        NavigationContent(StudentButton,"play","播放",expanded,_destination==0);
        NavigationContent(TeacherButton,"education","制作",expanded,_destination==1);
        NavigationContent(SettingsButton,"settings","设置",expanded,_destination==2);
        NavigationContent(QueueButton,"sync","转写任务",expanded,false,_services.Queue.Jobs.Count(j=>j.IsActive));
    }
    private static void NavigationContent(Button button,string icon,string label,bool expanded,bool selected,int count=0)
    {
        button.Classes.Set("selected",selected); ToolTip.SetTip(button,label);
        var content=new Grid{ColumnDefinitions=new ColumnDefinitions("4,36,*,Auto")};
        if(selected)content.Children.Add(new Border{Width=3,Height=20,Background=WorkspaceUi.Accent,CornerRadius=new CornerRadius(2),VerticalAlignment=VerticalAlignment.Center});
        var glyph=WorkspaceUi.Icon(icon);Grid.SetColumn(glyph,1);content.Children.Add(glyph);
        if(expanded){var text=WorkspaceUi.Text(label);text.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(text,2);content.Children.Add(text);}
        if(count>0&&expanded){var badge=WorkspaceUi.Text(count.ToString(),12);badge.Foreground=Brushes.White;var border=new Border{Background=WorkspaceUi.Accent,CornerRadius=new CornerRadius(10),Padding=new Thickness(6,1),Margin=new Thickness(8,0,12,0),VerticalAlignment=VerticalAlignment.Center,Child=badge};Grid.SetColumn(border,3);content.Children.Add(border);}
        button.Content=content;
    }
    private async Task ShowTasksAsync()
    {
        if(_taskDialog is not null)return;
        var width=Math.Min(ClientSize.Width-32,Math.Clamp(ClientSize.Width*.618,640,980));
        _taskDialog=AppDialogs.Create(this,"转写任务",_queue,width);
        _taskDialog.CloseButtonText="关闭";
        void ResizeQueue()
        {
            _queue.Width=Math.Min(width,ClientSize.Width-32)-50;
            _queue.Height=Math.Min(ClientSize.Height-32,Math.Clamp(ClientSize.Height*.72,480,720))-112;
        }
        void OnResize(object? sender,SizeChangedEventArgs args)=>ResizeQueue();
        ResizeQueue();SizeChanged+=OnResize;
        try{await _taskDialog.ShowAsync(this);}finally{SizeChanged-=OnResize;_taskDialog=null;}
    }
    private static void ApplyTheme(AppSettings settings){if(Avalonia.Application.Current is {} app)app.RequestedThemeVariant=settings.ThemeMode switch{"dark"=>ThemeVariant.Dark,"light"=>ThemeVariant.Light,_=>ThemeVariant.Default};}
    private async Task InitializeAsync()
    {
        try
        {
            await _services.InitializeAsync();ApplyTheme(_services.Settings);await _teacher.RefreshAsync();_teacherLoaded=true;_settings.Populate(_services.Settings);
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
        _taskDialog?.Hide();Navigate(1);await _teacher.PickAudioAndEnqueueAsync();
    }
    private static Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var completion=new TaskCompletionSource<T>();Dispatcher.UIThread.Post(async()=>{try{completion.SetResult(await action());}catch(Exception ex){completion.SetException(ex);}});return completion.Task;
    }
}
