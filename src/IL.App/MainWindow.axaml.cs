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
using IL.Core.Management;
using System.Text.Json.Nodes;

namespace IL.App;

public partial class MainWindow : FluentAvalonia.UI.Windowing.FAAppWindow
{
    private readonly AppServices _services;
    private readonly StudentView _student;
    private readonly TeacherView _teacher;
    private readonly SettingsView _settings;
    private bool _closing, _paneExpanded = true, _teacherLoaded;
    private bool _openingTeacher;
    private int _destination, _settingsBack;
    private string? _openedAudioPath;
    private FAContentDialog? _taskDialog;
    private readonly SpringScalar _paneMotion;
    private readonly DesktopResidence? _residence;
    private WindowsApprovalNotification? _approvalNotification;
    private WindowsSystemNotification? _updateNotification;
    private bool _initializationStarted;
    private readonly TaskCompletionSource<bool> _presentation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _backgroundLaunch = Program.Arguments.Contains("--background-after-update");
    internal Task InitializeBackgroundAsync() => StartInitializationAsync();
    private Task StartInitializationAsync()
    {
        if (_initializationStarted) return Task.CompletedTask;
        _initializationStarted = true;
        return InitializeAsync();
    }
    private long _managedRevision=-1;
    private readonly HashSet<Key> _heldPlayerKeys = new();
    private bool _playerShortcutBusy;
    private bool _installingUpdate;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _servicesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public MainWindow()
    {
        InitializeComponent();
        Icon = AppBrand.Image;
        TitleBar.ExtendsContentIntoTitleBar = false;
        if (OperatingSystem.IsWindows()) WindowState = WindowState.Maximized;
        _paneMotion = new(this, 320, width => ShellGrid.ColumnDefinitions[0].Width = new GridLength(width));
        Closed += (_, _) => _paneMotion.Stop();
        var standalone=Program.Arguments.Contains("--standalone");
        _services=new(action=>Dispatcher.UIThread.Post(action),standalone);
        AgentProgressHost.Content = new AgentProgressView(_services.Server.WorkflowProgress);
        _student=new(_services.Student, standalone ? null : new CloudLessonsView(_services));
        AddHandler(KeyDownEvent, OnPlayerKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPlayerKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _heldPlayerKeys.Clear();
        _teacher=new(_services.Projects,_services.Queue,()=>_services.Settings,_services.LibraryDirectory,()=>Dispatcher.UIThread.Post(async()=>await _services.Student.RefreshAsync()),()=>ShowSettings(),_services.Transcriptions,_services.Server,EnableMcpAsync);
        _student.ImportAudio = async file =>
        {
            ManagedRuntime.Require("importAudio");
            var playbackFile = file;
            if (_services.Settings.CreateProjectOnAudioImport && ManagedRuntime.Allows("authoring"))
            {
                var project = await _services.Projects.CreateAsync(file);
                playbackFile = project.AudioPath!;
                await _teacher.RefreshAsync();
                WorkspaceToast.Show(this, "制作项目已建立", project.Title);
            }
            await _services.Student.OpenAudioAsync(playbackFile);
            if (_services.Student.AudioPath != playbackFile || !_services.Student.HasMedia || _services.Student.DurationSeconds <= 0)
                throw new IOException(_services.Student.Status);
        };
        _settings=new(_services.SettingsStore,async settings=>{ApplyTheme(settings);await _services.ApplySettingsAsync(settings);_residence?.UpdateMcpState(_services.Server.IsRunning);},_services.Server,
            standalone ? null : new CloudSettingsView(_services, () => _settings?.Populate(_services.Settings)), standalone ? null : _services.CloudAsr, standalone ? null : _services.CloudAccount);
        _teacher.CloudTransfers = async () =>
        {
            ManagedRuntime.Require("authoring");
            if (!_services.CloudAccount.Enabled) { ShowSettings(); _settings.ShowCloud(); return; }
            var cloud = new CloudAuthoringView(_services, _teacher.FlushDraftAsync, async () => { await _services.Student.RefreshAsync(preservePlayback: true); await _teacher.RefreshAsync(); });
            var dialog = AppDialogs.Create(this, "Intensive Listening 云服务", new ScrollViewer { Content = cloud, MaxHeight = Math.Max(240, ClientSize.Height - 200) }, Math.Min(1000, ClientSize.Width - 60));
            dialog.CloseButtonText = "关闭"; await dialog.ShowAsync(this);
        };
        _settings.ConfigureUpdates(_services.Updates, _services.ManagedUpdates, InstallManagedUpdateAsync);
        _settings.ConfigureManagedLessons(_services.ManagedLessons);
        _services.ManagedLessons.IsInUse = uuid => OnUiAsync(() => Task.FromResult(_services.Student.CurrentLesson?.Id.Equals(uuid, StringComparison.OrdinalIgnoreCase) == true || _teacher.HasOpenProject));
        _services.ManagedLessons.LibraryChanged = () => OnUiAsync(async () => { await _services.Student.RefreshAsync(preservePlayback: true); return true; });
        _services.ManagedLessons.Imported += lesson => Dispatcher.UIThread.Post(() =>
        {
            var manifest = _services.Student.Lessons.FirstOrDefault(l => l.Id.Equals(lesson.PackageUuid, StringComparison.OrdinalIgnoreCase))?.Lesson.Manifest;
            WorkspaceToast.Show(this, "组织精听包已导入", $"{manifest?.Title ?? lesson.Title} · 版本 {lesson.PackageVersion}\n{manifest?.CreatorDescription}");
        });
        RefreshManagedBanner();
        if (OperatingSystem.IsWindows()) _services.ManagedUpdates.AutomaticInstall=()=>OnUiAsync(async()=>{await AutomaticManagedUpdateAsync();return true;});
        _services.ManagedUpdates.NotificationRequested += update => Dispatcher.UIThread.Post(async () =>
        {
            var title = "组织更新已下载";
            var message = $"版本 {update.Version} 已下载并校验，点击更新开始安装。";
            if (OperatingSystem.IsWindows())
            {
                _updateNotification ??= new WindowsSystemNotification(this, 0x494D, 0x8422);
                _updateNotification.Show(title, message, () => Dispatcher.UIThread.Post(async () => { if (update.Id != _services.ManagedUpdates.UpdateId) return; _pendingColdUpdate = null; RestoreWorkspace(); ShowSettings(); _settings.ShowUpdates(); WorkspaceToast.Show(this, "开始安装更新", "正在保存工程并准备安装。"); await InstallManagedUpdateAsync(); }));
            }
            _pendingColdUpdate = update;
            await ShowPendingColdUpdateAsync();
        });
        _services.Mdn.ApplyPolicyAsync = () => OnUiAsync(async () => { await ApplyManagedPolicyAsync(); return true; });
        _services.Mdn.Changed+=()=>Dispatcher.UIThread.Post(async()=>
        {
            RefreshManagedBanner();
            if(!await _servicesReady.Task)return;
            _ = _services.ManagedLessons.RefreshAsync();
            if(_managedRevision==_services.Mdn.Policy.Revision){_ = _services.ManagedUpdates.RefreshAsync();return;}
            await ApplyManagedPolicyAsync();
            _ = _services.ManagedUpdates.RefreshAsync();
        });
        _services.ConfirmForcedCuts=()=>OnUiAsync(()=>{RestoreWorkspace();return AppDialogs.ConfirmForcedCutsAsync(this);});
        _services.Server.ApprovalRequested=name=>OnUiAsync(()=>RequestAgentApprovalAsync(name));
        Closed += (_, _) => _approvalNotification?.Dispose();
        Closed += (_, _) => _updateNotification?.Dispose();
        _services.Server.AgentStateChanged+=active=>Dispatcher.UIThread.Post(async()=>{AgentTakeoverText.Text=$"当前 {_services.Server.ActiveAgentName ?? "未命名智能体"} 正在接管本应用";AgentOverlay.IsVisible=active;ShellGrid.IsEnabled=!active;ShellGrid.Effect=active?new BlurEffect{Radius=12}:null;_teacher.SetAgentMode(active);if(active)Navigate(1);else await _teacher.FinishAgentSessionAsync();});
        _services.Api.ProjectChanged=()=>Dispatcher.UIThread.Post(async()=>await _teacher.RefreshAsync());
        _services.Api.OpenProject=id=>Dispatcher.UIThread.Post(async()=>{if(!ManagedRuntime.Allows("authoring"))return;RestoreWorkspace();await _teacher.OpenProjectAsync(id);Navigate(1);});
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
        _services.Student.PropertyChanged += (_, e) => { if (!standalone && e.PropertyName == nameof(_services.Student.HasMedia) && !_services.Student.HasMedia) _ = _services.ManagedLessons.RefreshAsync(); };
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
        Activated += async (_, _) => await ShowPendingColdUpdateAsync();
        Opened += async (_, _) => { _presentation.TrySetResult(true); await StartInitializationAsync(); };
        Closed += (_, _) => _presentation.TrySetResult(false);
        if (OperatingSystem.IsWindows() && !standalone)
        {
            _residence = new(this, _teacher.FlushDraftAsync, async () => { await _settings.StopDiagnosticsAsync(); await _services.DisposeAsync(); }, () => (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
            _residence.UpdateOnClosePending = () => _otaPrompt is not null || _services.ManagedUpdates.CanInstallDeferred;
            _residence.UpdateOnClose = UpdateAfterCloseAsync;
        }
        else
            Closing+=async(_,e)=>{if(_closing)return;e.Cancel=true;await ExitAsync();};
    }
    private async Task ApplyManagedPolicyAsync()
    {
        var settings = await _services.SettingsStore.LoadAsync();
        await _services.ApplySettingsAsync(settings); ApplyTheme(settings); _settings.Populate(settings);
        _residence?.UpdateMcpState(_services.Server.IsRunning);
        if (!ManagedRuntime.Allows("playback")) await _services.Student.PauseAsync();
        _teacher.ApplyManagement(); _student.ApplyManagement(); UpdatePane();
        if (!ManagedRuntime.Allows("authoring") && _destination == 1) { await _teacher.FlushDraftAsync(); Navigate(0); }
        _managedRevision = _services.Mdn.Policy.Revision;
    }
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void RefreshManagedBanner()
    {
        var config = _services.Mdn.Configuration; ManagedBanner.IsVisible = config is not null;
        if (config is not null) WorkspaceUi.OrganizationText(ManagedBanner, "当前由", config.OrganizationName, $"管理 · {_services.Mdn.DeviceId.Split('-')[^1]}");
        else { ManagedBanner.Inlines = null; ManagedBanner.Text = ""; }
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
        if (args.Contains("--background-after-update")) return;
        RestoreWorkspace();
        if (!await _initialized.Task) return;
        await _launchGate.WaitAsync();
        try
        {
            foreach (var path in args.Where(p => p.EndsWith(".ilp", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
            {
                if ((_services.Student.HasMedia || _teacher.HasOpenProject) &&
                    !await AppDialogs.ConfirmAsync(this, "打开新的精听包？", "当前有正在打开的播放或制作内容。是否保存制作进度并打开新文件？", "打开文件")) continue;
                if (_services.Server.AgentActive) { WorkspaceToast.Show(this, "请先结束智能体接管", "结束接管后可以打开精听包。"); continue; }
                await _teacher.FlushDraftAsync(); await _services.Student.PauseAsync();
                Navigate(0); await _student.ImportFileAsync(path);
            }
        }
        catch (Exception error) { AppLog.Warning("打开精听包失败",error); WorkspaceToast.Show(this,"无法打开精听包",error.Message,true); }
        finally { _launchGate.Release(); }
    }
    internal async Task ExitAsync()
    {
        if (_residence != null) { await _residence.ExitAsync(); return; }
        if (_closing) return; _closing=true;
        try { await _teacher.FlushDraftAsync();await _settings.StopDiagnosticsAsync();await _services.DisposeAsync(); }
        catch(Exception ex){AppLog.Warning("关闭时保存失败",ex);}
        Close();
    }
    internal async Task RestartAsync()
    {
        await _teacher.FlushDraftAsync();
        if (!CrashMonitor.Launch(["--restart-after-exit", Environment.ProcessId.ToString(), .. Program.Arguments.Where(arg => arg != "--background-after-update")]))
            throw new IOException("无法启动重启进程。");
        await ExitAsync();
    }
    private async Task InstallManagedUpdateAsync()
    {
        if (_installingUpdate) return;
        _installingUpdate = true;
        if (_services.ManagedUpdates.UpdateMode == "cold") { _services.ManagedUpdates.ReopenAfterUpdate = true; _services.ManagedUpdates.SkipUpdateOobe = false; }
        try { if (!await _services.ManagedUpdates.PrepareAsync()) return; await _teacher.FlushDraftAsync();await _services.ManagedUpdates.LaunchInstallerAsync();await ExitAsync(); }
        catch(Exception e){WorkspaceToast.Show(this,"更新未完成",e.Message,true);}
        finally { _installingUpdate = false; }
    }
    private CancellationTokenSource? _otaPrompt;
    private IL.Core.Management.ManagedUpdate? _pendingColdUpdate;
    private async Task ShowPendingColdUpdateAsync()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _pendingColdUpdate is not {} update) return;
        _pendingColdUpdate = null;
        if (update.Id != _services.ManagedUpdates.UpdateId) return;
        var choice = await AppDialogs.ChooseAsync(this, "组织更新已下载", $"版本 {update.Version} 已下载并校验，点击更新开始安装。", "稍后", "更新");
        ShowSettings(); _settings.ShowUpdates();
        if (choice == 1 && update.Id == _services.ManagedUpdates.UpdateId) await InstallManagedUpdateAsync();
    }
    private bool _updateOnClose;
    private async Task AutomaticManagedUpdateAsync()
    {
        var visible = IsVisible && WindowState != WindowState.Minimized;
        _services.ManagedUpdates.ReopenAfterUpdate = visible;
        _services.ManagedUpdates.SkipUpdateOobe = visible;
        if (visible)
        {
            _otaPrompt = new();
            bool install;
            try { install = await AppDialogs.UpdateCountdownAsync(this, _otaPrompt.Token); }
            finally { _otaPrompt.Dispose(); _otaPrompt = null; }
            if (_updateOnClose) return;
            if (!install)
            {
                _services.ManagedUpdates.DeferUntilClose();
                WorkspaceToast.Show(this, "关闭窗口后自动更新", "关闭窗口时将保存工程并安装更新。更新期间请勿关闭计算机。");
                return;
            }
        }
        await InstallManagedUpdateAsync();
    }
    private async Task UpdateAfterCloseAsync()
    {
        if (_updateOnClose || _installingUpdate) return;
        _updateOnClose = true; _otaPrompt?.Cancel();
        _services.ManagedUpdates.DeferUntilClose();
        WorkspaceToast.Show(this, "正在保存并开始更新", "更新期间请勿关闭计算机。");
        await InstallManagedUpdateAsync();
        _updateOnClose = false;
    }
    private void ShowSettings(){if(_destination==2)return;if(_destination!=2)_settingsBack=_destination;_settings.Populate(_services.Settings);Navigate(2);}
    private void Navigate(int destination)
    {
        if (destination == 1 && !ManagedRuntime.Allows("authoring")) destination = 0;
        AppLog.Operation("navigation", $"切换页面 from={_destination} to={destination}");
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
        _student.RegisterInteraction();
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
        if (!ManagedRuntime.Allows("authoring") || _openingTeacher || _destination == 1) return;
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
        TeacherButton.IsEnabled = ManagedRuntime.Allows("authoring"); TeacherButton.Opacity = TeacherButton.IsEnabled ? 1 : .45;
        QueueButton.IsEnabled = ManagedRuntime.Allows("asr"); QueueButton.Opacity = QueueButton.IsEnabled ? 1 : .45;
        NavigationContent(StudentButton,"play","播放",expanded,_destination==0);
        NavigationContent(TeacherButton,"education","制作",expanded,_destination==1);
        NavigationContent(SettingsButton,"settings","设置",expanded,_destination==2);
        NavigationContent(QueueButton,"sync","转写任务",expanded,false,_services.Queue.Jobs.Count(j=>j.IsActive));
    }
    private static void NavigationContent(Button button,string icon,string label,bool expanded,bool selected,int count=0)
    {
        button.Classes.Set("selected",selected); ToolTip.SetTip(button,label);
        var content=new Grid{ColumnDefinitions=new ColumnDefinitions(expanded ? "4,36,*,Auto" : "4,*,4")};
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
        if(!ManagedRuntime.Allows("asr") || _taskDialog is not null)return;
        AppLog.Operation("queue", "打开转写队列");
        var queue=new QueueView(_services.Queue,async job=>{_taskDialog?.Hide();await _teacher.LoadJobTranscriptAsync(job);Navigate(1);},async match=>{if(match.LessonId is {} id){_taskDialog?.Hide();Navigate(0);_services.Student.SelectedLesson=_services.Student.Lessons.FirstOrDefault(l=>l.Id==id);await _services.Student.CurrentLoad;}},()=>PickAudioForQueueAsync());
        var width=Math.Min(ClientSize.Width-32,Math.Clamp(ClientSize.Width*.618,640,980));
        _taskDialog=AppDialogs.Create(this,"转写任务",queue,width);
        _taskDialog.CloseButtonText="关闭";
        void ResizeQueue()
        {
            queue.Width=Math.Min(width,ClientSize.Width-32)-50;
            queue.Height=Math.Min(ClientSize.Height-32,Math.Clamp(ClientSize.Height*.72,480,720))-112;
        }
        void OnResize(object? sender,SizeChangedEventArgs args)=>ResizeQueue();
        ResizeQueue();SizeChanged+=OnResize;
        try{await _taskDialog.ShowAsync(this);}finally{SizeChanged-=OnResize;queue.Dispose();_taskDialog=null;AppLog.Operation("queue", "关闭转写队列");}
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
            var installationId=DeviceIdentity.InstallationCycle(_services.DataDirectory);
            var updateResult=Path.Combine(_services.DataDirectory,"updates","result.json");
            if(File.Exists(updateResult))
            {
                var result=JsonNode.Parse(await File.ReadAllTextAsync(updateResult));
                _services.ManagedUpdates.RecordResult(result?["id"]?.ToString() ?? "", result?["version"]?.ToString(), result?["success"]?.GetValue<bool>() == true);
                if (result?["success"]?.GetValue<bool>() == true && IL.Core.Updates.ManagedUpdateJournal.IsSameVersion(AppBuildInfo.Version, result?["version"]?.ToString())) AppBuildInfo.SuccessfulUpdateVersion = AppBuildInfo.DisplayVersion;
                if (Guid.TryParse(result?["id"]?.ToString(), out var resultId))
                    await _services.Mdn.ReportUpdateAsync(resultId.ToString(), result?["success"]?.GetValue<bool>() == true && IL.Core.Updates.ManagedUpdateJournal.IsSameVersion(AppBuildInfo.Version, result?["version"]?.ToString()) ? "success" : "failed", result?["success"]?.GetValue<bool>() == true ? "更新安装完成" : "安装未完成或用户取消 UAC");
                if(result?["success"]?.GetValue<bool>()==true && IL.Core.Updates.ManagedUpdateJournal.IsSameVersion(AppBuildInfo.Version, result?["version"]?.ToString()) && result?["automaticOobe"]?.GetValue<bool>()==true && _services.Settings.EulaAcceptedVersion.Length>0 && ManagedRuntime.IsManaged)
                { var completed=_services.Settings with{CompletedInstallationId=installationId};await _services.SettingsStore.SaveAsync(completed);await _services.ApplySettingsAsync(completed); }
                if (result?["success"]?.GetValue<bool>() != true) File.Delete(updateResult);
            }
            if (_backgroundLaunch)
            {
                // Receive policy and report update completion while onboarding waits for a user.
                await _services.ApplySettingsAsync(_services.Settings);
                _servicesReady.TrySetResult(true);
                _residence?.UpdateMcpState(_services.Server.IsRunning);
                _services.Mdn.Start();
                _ = _services.ManagedUpdates.RefreshAsync();
                _ = _services.ManagedLessons.RefreshAsync();
                if (!await _presentation.Task) return;
            }
            if(!_services.Standalone&&_services.Settings.NeedsOnboarding(installationId))
            {
                var legal=Path.Combine(AppContext.BaseDirectory,"assets","legal");
                var settings=await AppDialogs.FirstRunAsync(this,_services.Settings,await File.ReadAllTextAsync(Path.Combine(legal,"eula_zh_cn.txt")),await File.ReadAllTextAsync(Path.Combine(legal,"privacy_zh_cn.txt")),InstallationOnboarding.WelcomeOnly(_services.DataDirectory), _services.CloudAccount);
                if(settings is null){await ExitAsync();return;}settings=settings with { CompletedInstallationId=installationId };await _services.SettingsStore.SaveAsync(settings);_settings.Populate(settings);ApplyTheme(settings);await _services.ApplySettingsAsync(settings);
            }
            else await _services.ApplySettingsAsync(_services.Settings);
            if (File.Exists(updateResult)) File.Delete(updateResult);
            if (!_services.Standalone) _ = _services.Updates.CheckAsync(AppBuildInfo.UpdateVersion);
            if (_services.PreconfigurationError is {} presetError) WorkspaceToast.Show(this, "预设配置", presetError, true);
            _residence?.UpdateMcpState(_services.Server.IsRunning);
            for(var i=0;i<Program.Arguments.Length;i++)
            {
                var file=Program.Arguments[i];if(file.EndsWith(".ilp",StringComparison.OrdinalIgnoreCase)&&File.Exists(file)){await _student.ImportFileAsync(file,_services.Standalone);break;}
            }
            await AppLog.WriteAsync("INFO","应用初始化完成");
            foreach (var report in new CrashReportStore(_services.DataDirectory).Reports().Where(r => !r.Displayed))
                new CrashReportWindow(new CrashReportStore(_services.DataDirectory).PathFor(report.Id), submit: false).Show(this);
            if(_services.Queue.InterruptedCount>0&&await AppDialogs.ConfirmAsync(this,"恢复转写任务",$"发现 {_services.Queue.InterruptedCount} 个未完成的任务，继续处理？","恢复"))_services.Queue.ResumeInterrupted();
            _servicesReady.TrySetResult(true);
            _initialized.TrySetResult(true);
            _teacher.ApplyManagement();_student.ApplyManagement();UpdatePane();if(!ManagedRuntime.Allows("authoring")&&_destination==1){await _teacher.FlushDraftAsync();Navigate(0);}
            if(!_services.Standalone){_services.Mdn.Start();_ = _services.ManagedUpdates.RefreshAsync();_ = _services.ManagedLessons.RefreshAsync();}
        }
        catch(Exception ex){AppLog.Warning("初始化失败",ex);_services.Student.Status=$"初始化失败：{ex.Message}";}
        finally { _servicesReady.TrySetResult(false); _initialized.TrySetResult(false); }
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
