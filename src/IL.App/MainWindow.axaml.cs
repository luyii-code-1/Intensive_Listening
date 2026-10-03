using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private readonly SpringScalar _paneMotion;
    private readonly DesktopResidence? _residence;
    private WindowsApprovalNotification? _approvalNotification;
    private bool _initializationStarted;
    private readonly HashSet<Key> _heldPlayerKeys = new();
    private bool _playerShortcutBusy;
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public MainWindow()
    {
        InitializeComponent();
        Icon = AppBrand.WindowIcon();
        _paneMotion = new(this, 320, width => ShellGrid.ColumnDefinitions[0].Width = new GridLength(width));
        Closed += (_, _) => _paneMotion.Stop();
        var standalone=Program.Arguments.Contains("--standalone");
        _services=new(action=>Dispatcher.UIThread.Post(action),standalone);
        AgentProgressHost.Content = new AgentProgressView(_services.Server.WorkflowProgress);
        _student=new(_services.Student);
        AddHandler(KeyDownEvent, OnPlayerKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPlayerKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _heldPlayerKeys.Clear();
        _teacher=new(_services.Projects,_services.Queue,()=>_services.Settings,_services.LibraryDirectory,()=>Dispatcher.UIThread.Post(async()=>await _services.Student.RefreshAsync()),()=>ShowSettings(),_services.Transcriptions,_services.Server,EnableMcpAsync);
        _settings=new(_services.SettingsStore,async settings=>{ApplyTheme(settings);await _services.ApplySettingsAsync(settings);_residence?.UpdateMcpState(_services.Server.IsRunning);},_services.Server);
        _queue=new(_services.Queue,async job=>{_taskDialog?.Hide();await _teacher.LoadJobTranscriptAsync(job);Navigate(1);},async match=>{if(match.LessonId is {} id){_taskDialog?.Hide();Navigate(0);_services.Student.SelectedLesson=_services.Student.Lessons.FirstOrDefault(l=>l.Id==id);await _services.Student.CurrentLoad;}},()=>PickAudioForQueueAsync());
        _services.ConfirmForcedCuts=()=>OnUiAsync(()=>{RestoreWorkspace();return AppDialogs.ConfirmForcedCutsAsync(this);});
        _services.Server.ApprovalRequested=name=>OnUiAsync(()=>RequestAgentApprovalAsync(name));
        Closed += (_, _) => _approvalNotification?.Dispose();
        _services.Server.AgentStateChanged+=active=>Dispatcher.UIThread.Post(async()=>{AgentTakeoverText.Text=$"当前 {_services.Server.ActiveAgentName ?? "未命名智能体"} 正在接管本应用";AgentOverlay.IsVisible=active;ShellGrid.IsEnabled=!active;ShellGrid.Effect=active?new BlurEffect{Radius=12}:null;_teacher.SetAgentMode(active);if(active)Navigate(1);else await _teacher.FinishAgentSessionAsync();});
        _services.Api.ProjectChanged=()=>Dispatcher.UIThread.Post(async()=>await _teacher.RefreshAsync());
        _services.Api.OpenProject=id=>Dispatcher.UIThread.Post(async()=>{RestoreWorkspace();await _teacher.OpenProjectAsync(id);Navigate(1);});
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
            await ExitAsync();
        };
        SizeChanged+=(_,_)=>UpdatePane();
        _services.Queue.Changed+=(_,_)=>Dispatcher.UIThread.Post(UpdatePane);
        Navigate(0);
        if(standalone){NavigationPane.IsVisible=false;ShellGrid.ColumnDefinitions[0].Width=new GridLength(0);Width=1120;}
        void BackgroundFault(CrashReport report) => Dispatcher.UIThread.Post(async () =>
        {
            RestoreWorkspace();
            await new CrashReportStore(_services.DataDirectory).FlushAsync(_services.Settings.TelemetryEnabled, _services.Telemetry.ReportCrashAsync);
            var reportWindow = new CrashReportWindow(new CrashReportStore(_services.DataDirectory).PathFor(report.Id), submit: false);
            reportWindow.Show(this);
        });
        CrashMonitor.BackgroundFault += BackgroundFault;
        Closed += (_, _) => CrashMonitor.BackgroundFault -= BackgroundFault;
        Opened+=async(_,_)=>{if(_initializationStarted)return;_initializationStarted=true;await InitializeAsync();};
        if (OperatingSystem.IsWindows() && !standalone)
            _residence = new(this, _teacher.FlushDraftAsync, async () => await _services.DisposeAsync(), () => (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        else
            Closing+=async(_,e)=>{if(_closing)return;e.Cancel=true;await ExitAsync();};
    }
    private void RestoreWorkspace() { if (_residence != null) _residence.Restore(); else { Show(); if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate(); } }
    private async Task<IL.Core.Mcp.AgentApprovalDecision> RequestAgentApprovalAsync(string name)
    {
        var foreground = IsVisible && WindowState != WindowState.Minimized && (IsActive || OwnedWindows.Any(w => w.IsActive));
        if (OperatingSystem.IsWindows() && !foreground)
        {
            var presentation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnActivated(object? sender, EventArgs args) => presentation.TrySetResult(true);
            void OnClosed(object? sender, EventArgs args) => presentation.TrySetResult(false);
            void OnSessionChanged()
            {
                if (!_services.Server.ApprovalPending) presentation.TrySetResult(false);
            }
            Activated += OnActivated; Closed += OnClosed;
            _services.Server.AgentsChanged += OnSessionChanged;
            try
            {
                _approvalNotification ??= new WindowsApprovalNotification(this);
                _approvalNotification.Show(name, () =>
                {
                    if (!_services.Server.ApprovalPending) return;
                    RestoreWorkspace(); presentation.TrySetResult(true);
                });
                OnSessionChanged();
                if (!await presentation.Task || !_services.Server.ApprovalPending)
                    return IL.Core.Mcp.AgentApprovalDecision.Refuse;
            }
            finally
            {
                Activated -= OnActivated; Closed -= OnClosed;
                _services.Server.AgentsChanged -= OnSessionChanged;
                _approvalNotification?.Close();
            }
        }
        RestoreWorkspace();
        _taskDialog?.Hide();
        var decision = await AppDialogs.ApproveAgentAsync(this, name);
        if (decision == IL.Core.Mcp.AgentApprovalDecision.Approve) await _teacher.PrepareAgentSessionAsync();
        else if (decision == IL.Core.Mcp.AgentApprovalDecision.DisableMcp)
        {
            var settings = _services.Settings with { McpEnabled = false };
            await _services.SettingsStore.SaveAsync(settings); await _services.ApplySettingsAsync(settings);
            _residence?.UpdateMcpState(false);
        }
        return decision;
    }
    internal async Task ActivateFromLaunchAsync(string[] args)
    {
        RestoreWorkspace();
        if (!await _initialized.Task) return;
        try { foreach (var path in args.Where(p => p.EndsWith(".ilp",StringComparison.OrdinalIgnoreCase) && File.Exists(p))) { Navigate(0); await _student.ImportFileAsync(path); } }
        catch (Exception error) { AppLog.Warning("打开精听包失败",error); WorkspaceToast.Show(this,"无法打开精听包",error.Message,true); }
    }
    internal async Task ExitAsync()
    {
        if (_residence != null) { await _residence.ExitAsync(); return; }
        if (_closing) return; _closing=true;
        try { await _teacher.FlushDraftAsync();await _services.DisposeAsync(); }
        catch(Exception ex){AppLog.Warning("关闭时保存失败",ex);}
        Close();
    }
    private void ShowSettings(){if(_destination==2)return;if(_destination!=2)_settingsBack=_destination;_settings.Populate(_services.Settings);Navigate(2);}
    private void Navigate(int destination)
    {
        _heldPlayerKeys.Clear();
        _destination=destination;PageHost.Content=destination switch{1=>_teacher,2=>_settings,_=>_student};
        UpdatePane();
    }
    private async void OnPlayerKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsActive || !IsVisible || WindowState == WindowState.Minimized || _destination != 0 ||
            PageHost.Content != _student || !ShellGrid.IsEnabled || !_services.Student.HasMedia) return;
        if (e.Source is not Control source) return;
        if (!PlayerShortcuts.AllowsSource(source, this, ShellGrid) ||
            this.GetVisualDescendants().OfType<FAContentDialog>().Any(d => d.IsVisible)) return;
        if (!PlayerShortcuts.TryGetAction(_services.Settings.PlayerKeyBindings, e.Key, e.KeyModifiers, out var action)) return;
        e.Handled = true;
        if (!_heldPlayerKeys.Add(e.Key) || _playerShortcutBusy || !_services.Student.CanPlay) return;
        var player = _services.Student;
        if (action != PlayerShortcutAction.TogglePlayback && !player.HasTranscript) return;
        _playerShortcutBusy = true;
        try
        {
            if (action == PlayerShortcutAction.ToggleSubtitles) player.ShowSubtitles = !player.ShowSubtitles;
            else await (action switch
            {
                PlayerShortcutAction.TogglePlayback => player.TogglePlaybackCommand,
                PlayerShortcutAction.PreviousCue => player.PreviousCueCommand,
                PlayerShortcutAction.NextCue => player.NextCueCommand,
                PlayerShortcutAction.PreviousQuestion => player.PreviousQuestionCommand,
                PlayerShortcutAction.NextQuestion => player.NextQuestionCommand,
                PlayerShortcutAction.ReplayCue => player.RepeatSentenceCommand,
                _ => throw new InvalidOperationException("未知播放操作")
            }).ExecuteAsync(null);
        }
        catch (Exception ex) { AppLog.Warning("扩展按键操作失败", ex); WorkspaceToast.Show(this, "播放操作失败", ex.Message, true); }
        finally { _playerShortcutBusy = false; }
    }
    private void OnPlayerKeyUp(object? sender, KeyEventArgs e)
    {
        if (_heldPlayerKeys.Remove(e.Key)) e.Handled = true;
    }
    private async Task ShowTeacherAsync()
    {
        if (_openingTeacher || _destination == 1) return;
        var fromStudent = _destination == 0;
        _openingTeacher = true; NavigationBusy.IsVisible = true; _teacher.IsHitTestVisible = false;
        var source = _destination;
        try
        {
            await _services.Student.PauseAsync();
            if (fromStudent) await _teacher.ReturnToProjectsAsync();
            if (!_teacherLoaded) { await _teacher.RefreshAsync(); _teacherLoaded = true; }
            if (_destination == source) Navigate(1);
        }
        catch (Exception ex) { AppLog.Warning("打开制作页面失败", ex); WorkspaceToast.Show(this, "无法打开制作页面", ex.Message, true); }
        finally { _openingTeacher = false; NavigationBusy.IsVisible = false; _teacher.IsHitTestVisible = true; }
    }
    private void CollapsePane(){_paneExpanded=false;UpdatePane();}
    private void UpdatePane()
    {
        if(_services.Standalone)return;
        var expanded=_paneExpanded&&ClientSize.Width>=1200;
        var paneWidth = expanded ? 320 : 48;
        if (IsVisible) _ = _paneMotion.To(paneWidth, FirstRunWizard.MotionReduced() ? .2 : .32);
        else _paneMotion.Set(paneWidth);
        NavigationContent(StudentButton,"play","播放",expanded,_destination==0);
        NavigationContent(TeacherButton,"education","制作",expanded,_destination==1);
        NavigationContent(SettingsButton,"settings","设置",expanded,_destination==2);
        NavigationContent(QueueButton,"sync","转写任务",expanded,false,_services.Queue.Jobs.Count(j=>j.IsActive));
    }
    private static void NavigationContent(Button button,string icon,string label,bool expanded,bool selected,int count=0)
    {
        button.Classes.Set("selected",selected); ToolTip.SetTip(button,label);
        var content=new Grid{ColumnDefinitions=new ColumnDefinitions("4,36,*,Auto")};
        if(selected)
        {
            var indicator = new Border { Width = 3, Height = 20, Background = WorkspaceUi.Accent, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            SpringMotion.Entrance(indicator, 0, .8); content.Children.Add(indicator);
        }
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
    private static void ApplyTheme(AppSettings settings)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = settings.ThemeMode switch { "dark" => ThemeVariant.Dark, "light" => ThemeVariant.Light, _ => ThemeVariant.Default };
        ApplicationTypography.Apply(settings);
    }
    private async Task InitializeAsync()
    {
        try
        {
            await _services.InitializeAsync();ApplyTheme(_services.Settings);await _teacher.RefreshAsync();_teacherLoaded=true;_settings.Populate(_services.Settings);
            var installationPath=Path.Combine(AppContext.BaseDirectory,"installation-id.txt");
            var installationId=File.Exists(installationPath)?(await File.ReadAllTextAsync(installationPath)).Trim():"";
            if(!_services.Standalone&&_services.Settings.NeedsOnboarding(installationId))
            {
                var legal=Path.Combine(AppContext.BaseDirectory,"assets","legal");
                var settings=await AppDialogs.FirstRunAsync(this,_services.Settings,await File.ReadAllTextAsync(Path.Combine(legal,"eula_zh_cn.txt")),await File.ReadAllTextAsync(Path.Combine(legal,"privacy_zh_cn.txt")));
                if(settings is null){await ExitAsync();return;}settings=settings with { CompletedInstallationId=installationId };await _services.SettingsStore.SaveAsync(settings);_settings.Populate(settings);ApplyTheme(settings);await _services.ApplySettingsAsync(settings);
            }
            else await _services.ApplySettingsAsync(_services.Settings);
            _residence?.UpdateMcpState(_services.Server.IsRunning);
            for(var i=0;i<Program.Arguments.Length;i++)
            {
                var file=Program.Arguments[i];if(file.EndsWith(".ilp",StringComparison.OrdinalIgnoreCase)&&File.Exists(file)){await _student.ImportFileAsync(file,_services.Standalone);break;}
            }
            await AppLog.WriteAsync("INFO","应用初始化完成");
            foreach (var report in new CrashReportStore(_services.DataDirectory).Reports().Where(r => !r.Displayed))
                new CrashReportWindow(new CrashReportStore(_services.DataDirectory).PathFor(report.Id), submit: false).Show(this);
            if(_services.Queue.InterruptedCount>0&&await AppDialogs.ConfirmAsync(this,"恢复转写任务",$"发现 {_services.Queue.InterruptedCount} 个未完成的任务，继续处理？","恢复"))_services.Queue.ResumeInterrupted();
            _initialized.TrySetResult(true);
        }
        catch(Exception ex){AppLog.Warning("初始化失败",ex);_services.Student.Status=$"初始化失败：{ex.Message}";}
        finally { _initialized.TrySetResult(false); }
    }
    private async Task PickAudioForQueueAsync()
    {
        _taskDialog?.Hide();Navigate(1);await _teacher.PickAudioAndEnqueueAsync();
    }
    private async Task EnableMcpAsync()
    {
        var settings = _services.Settings with { McpEnabled = true };
        await _services.ApplySettingsAsync(settings); await _services.SettingsStore.SaveAsync(settings);
        _settings.Populate(settings); _residence?.UpdateMcpState(_services.Server.IsRunning);
    }
    private static Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var completion=new TaskCompletionSource<T>();Dispatcher.UIThread.Post(async()=>{try{completion.SetResult(await action());}catch(Exception ex){completion.SetException(ex);}});return completion.Task;
    }
}
