using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.VisualTree;
using IL.App.ViewModels;
using IL.App.Views.Dialogs;
using IL.Core.Ilp;
using IL.Core.Audio;
using IL.Core.Models;
using IL.Core.Projects;
using IL.Core.Settings;
using IL.Core.Transcription;

namespace IL.App.Views;

public sealed class TeacherView : UserControl
{
    public Action? ProjectOpened { get; set; }
    private readonly CourseProjectStore _store;
    private readonly TranscriptionQueue _queue;
    private readonly Func<AppSettings> _settings;
    private readonly string _libraryDirectory;
    private readonly Action _libraryChanged;
    private readonly Action _openSettings;
    private readonly TeacherWorkspaceViewModel _vm;
    private readonly ListBox _projectList = new() { Background = Brushes.Transparent };
    private readonly TextBox _title = WorkspaceUi.Input(hint: "项目名称");
    private readonly TextBox _transcript = WorkspaceUi.Input(hint: "SRT 字幕", multiline: true);
    private readonly Grid _editor = new();
    private readonly StackPanel _projectCommands = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Grid _content = new() { ColumnDefinitions = new ColumnDefinitions("300,1,*") };
    private readonly Border _busyOverlay = new() { IsVisible = false };
    private readonly Button _back = new();
    private readonly ProgressBar _steps = new() { Minimum = 0, Maximum = 3, Height = 4 };
    private readonly Grid _stepLabels = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
    private readonly WorkspaceContentHost _stage = new();
    private string? _stageKey;
    private readonly TextBlock _emptyProjects = WorkspaceUi.Text("暂无项目", 12);
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly HashSet<int> _selected = [];
    private readonly HashSet<string> _expandedRepeats = [];
    private IReadOnlyList<SrtCue> _editingCues = [];
    private readonly Dictionary<int, Control> _cueRows = [];
    private ScrollViewer? _reviewScroll;
    private ListBox? _reviewList;
    private readonly Dictionary<int, int> _cueItemIndexes = [];
    private SrtTranscriptStructure? _reviewStructure;
    private Button? _createQuestion;
    private bool _loading, _saving, _agentMode, _overview, _manual, _compact;
    private bool _exporting, _addingToLibrary;
    private bool? _dragSelection;
    private int? _dragAnchor, _dragSection;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CourseProject? _agentDraft;
    private string? _renderedProjectId;
    private Window? _owner;

    public TeacherView(CourseProjectStore store, TranscriptionQueue queue, Func<AppSettings> settings, string libraryDirectory, Action? libraryChanged = null, Action? openSettings = null)
    {
        _store = store; _queue = queue; _settings = settings; _libraryDirectory = libraryDirectory;
        _libraryChanged = libraryChanged ?? (() => { }); _openSettings = openSettings ?? (() => { }); _vm = new(store);
        Build();
        _title.TextChanged += (_, _) => ScheduleSave(); _transcript.TextChanged += (_, _) => ScheduleSave();
        _autosave.Tick += async (_, _) => { _autosave.Stop(); await RunAsync(SaveDraftAsync); };
        _projectList.SelectionChanged += async (_, _) => { if (!_loading && _projectList.SelectedItem is ListBoxItem { Tag: string id }) await OpenProjectAsync(id); };
        SizeChanged += (_, _) => { var compact = (TopLevel.GetTopLevel(this)?.ClientSize.Width ?? Bounds.Width) < 1220; if (_compact != compact) { _compact = compact; _content.ColumnDefinitions[0].Width = new GridLength(compact ? 240 : 300); BuildCommands(); } };
        AttachedToVisualTree += (_, _) => { _owner = TopLevel.GetTopLevel(this) as Window; _queue.Changed += QueueChanged; };
        DetachedFromVisualTree += async (_, _) => { _queue.Changed -= QueueChanged; _autosave.Stop(); await RunAsync(SaveDraftAsync); };
    }
    public Task RefreshAsync() => RunAsync(RefreshProjectsFromStoreAsync);
    private async Task RefreshProjectsFromStoreAsync()
    {
        await _vm.ReloadAsync(); RefreshProjects();
    }
    public Task ReturnToProjectsAsync() => RunAsync(CloseProjectAsync);
    public async Task OpenProjectAsync(string id) => await RunAsync(async () =>
    {
        await SaveDraftAsync(); await _vm.OpenAsync(id); RenderProject();
    });
    public async Task PrepareAgentSessionAsync()
    {
        await _operations.WaitAsync();
        try { await SaveDraftAsync(); SetAgentMode(true); }
        finally { _operations.Release(); }
    }
    public void SetAgentMode(bool active) { if (active && !_agentMode && _vm.Project is {} p && (p.Title != _title.Text || p.Transcript != _transcript.Text)) _agentDraft = p with { Title = _title.Text ?? p.Title, Transcript = _transcript.Text ?? p.Transcript }; _agentMode = active; _autosave.Stop(); IsEnabled = !active; _editor.IsEnabled = !active; WorkspaceToast.Show(this, active ? "智能体正在制作课程。结束接管后刷新工程。" : "已返回用户模式。"); }
    public async Task FinishAgentSessionAsync() => await RunAsync(async () =>
    {
        SetAgentMode(false); var id = _vm.Project?.Id;
        if (_agentDraft is {} draft)
        {
            var disk = await _store.LoadByIdAsync(draft.Id);
            if (disk != null && _owner != null)
            {
                var preserve = await AppDialogs.ChooseAsync(_owner, "工程有新的修改", "本地编辑与智能体制作结果均可保留。请选择载入智能体版本，或将本地编辑保存为新的工程副本。", "载入智能体版本", "保留本地草稿副本") == 1;
                if (preserve)
                {
                    var temp = Path.Combine(Path.GetTempPath(), "ilp-draft-" + Guid.NewGuid().ToString("N") + ".zip");
                    try { await _store.ExportZipToFileAsync(draft, temp); var copy = await _store.ImportZipAsync(temp); await _store.SaveAsync(copy with { Title = draft.Title + " 本地草稿", Transcript = draft.Transcript }); id = copy.Id; }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            _agentDraft = null;
        }
        await RefreshProjectsFromStoreAsync(); if (id != null) { await _vm.OpenAsync(id); RenderProject(); }
    });
    public async Task LoadJobTranscriptAsync(TranscriptionJob job) => await RunAsync(async () =>
    {
        if (job.ProjectId == null || string.IsNullOrWhiteSpace(job.Srt)) return;
        var project = await _store.LoadByIdAsync(job.ProjectId); if (project == null) return;
        if (job.SrtConsumed || project.TranscriptionJobId != job.Id) { await _vm.OpenAsync(project.Id); RenderProject(); return; }
        await _vm.SaveAsync(project with { Transcript = job.Srt, TranscriptionJobId = null, Step = CourseProjectStep.Review, ReviewPhase = ReviewPhase.Grouping, AutomaticQuestionPlanApplied = false, AutoQuestionPlanDeferred = false });
        _queue.MarkSrtConsumed(job.Id); await _vm.OpenAsync(project.Id); RenderProject(); WorkspaceToast.Show(this, "转写字幕已载入，请审阅题目与挖空。");
    });
    private void Build()
    {
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(24, 22, 24, 20), MinHeight = 36 };
        _back.Content = WorkspaceUi.Icon("back"); _back.Classes.Add("subtle"); _back.Padding = new Thickness(8); _back.Margin = new Thickness(0, 0, 8, 0);
        ToolTip.SetTip(_back, "返回项目列表"); _back.Click += async (_, _) => await RunAsync(CloseProjectAsync);
        header.Children.Add(_back);
        var heading = WorkspaceUi.Text("课程项目", 22, true); heading.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(heading, 1); header.Children.Add(heading);
        Grid.SetColumn(_projectCommands, 2); header.Children.Add(_projectCommands); page.Children.Add(header);
        var projects = new Grid { RowDefinitions = new RowDefinitions("*") , Margin = new Thickness(12, 4, 12, 16) };
        projects.Children.Add(_projectList); _emptyProjects.HorizontalAlignment = HorizontalAlignment.Center; _emptyProjects.VerticalAlignment = VerticalAlignment.Center; projects.Children.Add(_emptyProjects);
        _content.Children.Add(projects);
        var divider = new Border { Width = 1 }; divider.Bind(Border.BackgroundProperty, new DynamicResourceExtension("DividerStrokeColorDefaultBrush")); Grid.SetColumn(divider, 1); _content.Children.Add(divider);
        _editor.RowDefinitions = new RowDefinitions("Auto,*"); _editor.Margin = new Thickness(24, 4, 24, 24);
        _title.FontSize = 20; _title.MinHeight = 36;
        var progress = new StackPanel { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) }; progress.Children.Add(_steps); progress.Children.Add(_stepLabels);
        var titleArea = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch }; titleArea.Children.Add(_title); titleArea.Children.Add(progress);
        var aligned = new Border { Child = titleArea, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 760, Margin = new Thickness(0, 0, 0, 20) };
        aligned.Bind(WidthProperty, new Avalonia.Data.Binding("Bounds.Width") { Source = _editor, Converter = new TeacherWidthConverter() });
        Grid.SetRow(aligned, 0); _editor.Children.Add(aligned); Grid.SetRow(_stage, 1); _editor.Children.Add(_stage);
        Grid.SetColumn(_editor, 2); _content.Children.Add(_editor); Grid.SetRow(_content, 1); page.Children.Add(_content); Grid.SetRow(_busyOverlay, 1); page.Children.Add(_busyOverlay); Content = page;
        RenderProject();
    }
    private sealed class TeacherWidthConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object Convert(object? value, Type type, object? parameter, System.Globalization.CultureInfo culture) => value is double width ? Math.Max(0, Math.Min(760, width)) : 760d;
        public object ConvertBack(object? value, Type type, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
    private static Button MenuButton(string text, IEnumerable<(string Title, Func<Task> Action, bool Enabled)> entries)
    {
        var menu = new ContextMenu();
        foreach (var entry in entries)
        {
            var item = new MenuItem { Header = entry.Title, IsEnabled = entry.Enabled }; item.Click += async (_, _) => await entry.Action(); menu.Items.Add(item);
        }
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; label.Children.Add(WorkspaceUi.Text(text)); label.Children.Add(WorkspaceUi.Icon("chevron_down", 12));
        var button = new Button { Content = label }; button.Click += (_, _) => menu.Open(button); return button;
    }
    private void BuildCommands()
    {
        _projectCommands.Children.Clear(); _back.IsVisible = _vm.Project != null;
        var projects = _vm.Projects.Select(p => (p.Title, (Func<Task>)(() => OpenProjectAsync(p.Id)), true)).ToList();
        if (_compact)
        {
            var entries = projects.Select(p => ("打开：" + p.Item1, p.Item2, p.Item3)).ToList();
            entries.AddRange([("导入工程", () => RunAsync(ImportArchiveAsync), true), ("导出当前工程", () => RunAsync(ExportArchiveAsync), _vm.Project != null), ("导入 DOCX 试卷", () => RunAsync(ImportExamAsync), _vm.Project != null), ("导出试卷文本", () => RunAsync(ExportExamAsync), _vm.Project != null)]);
            _projectCommands.Children.Add(MenuButton("工程操作", entries));
        }
        else
        {
            if (projects.Count > 0) _projectCommands.Children.Add(MenuButton("选择工程", projects));
            _projectCommands.Children.Add(WorkspaceUi.Button("导入工程", () => RunAsync(ImportArchiveAsync)));
            var export = WorkspaceUi.Button("导出工程", () => RunAsync(ExportArchiveAsync)); export.IsEnabled = _vm.Project != null; _projectCommands.Children.Add(export);
            if (_vm.Project != null) _projectCommands.Children.Add(MenuButton("试卷", [("导入 DOCX", () => RunAsync(ImportExamAsync), true), ("导出 TXT", () => RunAsync(ExportExamAsync), true)]));
        }
        var create = WorkspaceUi.Button("新建项目", () => RunAsync(NewAsync), true);
        var createLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; createLabel.Children.Add(WorkspaceUi.Icon("add")); createLabel.Children.Add(WorkspaceUi.Text("新建项目")); create.Content = createLabel; _projectCommands.Children.Add(create);
        var settings = WorkspaceUi.IconButton("settings", "设置", () => { _openSettings(); return Task.CompletedTask; }); settings.Classes.Add("subtle"); ToolTip.SetTip(settings, "设置"); _projectCommands.Children.Add(settings);
    }
    private string ProjectStatus(CourseProject project)
    {
        var job = project.TranscriptionJobId is string id ? _queue.JobById(id) : null;
        return job?.Status switch
        {
            TranscriptionJobStatus.Running or TranscriptionJobStatus.Queued => $"正在转写 {Math.Round(Math.Clamp(job.Fraction ?? 0, 0, 1) * 100)}%",
            TranscriptionJobStatus.Interrupted => "转写已中断", TranscriptionJobStatus.Failed => "转写失败",
            _ => project.Step switch { CourseProjectStep.Audio => "等待音频", CourseProjectStep.Transcription => "等待转写", CourseProjectStep.Review => "待审阅", _ => "已完成" }
        };
    }
    private void QueueChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { RefreshProjects(); if (_vm.Project is { HasTranscript: false, HasAudio: true }) BuildStage(); });
    private void RefreshProjects()
    {
        _loading = true;
        _projectList.ItemsSource = _vm.Projects.Select(p =>
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4) };
            var title = WorkspaceUi.Text(p.Title); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
            var details = WorkspaceUi.Stack(title, WorkspaceUi.Text(ProjectStatus(p), 12)); details.Spacing = 3;
            if (p.TranscriptionJobId is string id && _queue.JobById(id) is { Status: TranscriptionJobStatus.Running } job) details.Children.Add(new ProgressBar { Height = 4, Value = (job.Fraction ?? 0) * 100 });
            row.Children.Add(details); var remove = WorkspaceUi.IconButton("delete", "删除项目", () => RunAsync(() => DeleteProjectAsync(p))); remove.Classes.Add("subtle"); remove.Padding = new Thickness(6); remove.Margin = new Thickness(8, 0, 0, 0); ToolTip.SetTip(remove, "删除项目"); Grid.SetColumn(remove, 1); row.Children.Add(remove);
            return new ListBoxItem { Tag = p.Id, Content = row, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(10, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        }).ToArray();
        _projectList.SelectedItem = _projectList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == _vm.Project?.Id);
        _emptyProjects.IsVisible = _vm.Projects.Count == 0; _loading = false; BuildCommands();
    }
    private void RenderProject()
    {
        if (_renderedProjectId != _vm.Project?.Id) { _questionEdits.Clear(); _selected.Clear(); _expandedRepeats.Clear(); _reviewList = null; _reviewScroll = null; _overview = _manual = false; _renderedProjectId = _vm.Project?.Id; if (_vm.Project != null) ProjectOpened?.Invoke(); }
        _loading = true; _title.Text = _vm.Project?.Title ?? ""; _transcript.Text = _vm.Project?.Transcript ?? ""; _editingCues = _vm.Cues.ToArray(); _loading = false;
        foreach (var child in _editor.Children) if (Grid.GetRow(child) == 0) child.IsVisible = _vm.Project != null;
        _editor.IsEnabled = !_agentMode; _back.IsVisible = _vm.Project != null; _steps.Value = (int)(_vm.Project?.Step ?? CourseProjectStep.Audio);
        _stepLabels.Children.Clear(); var labels = new[] { "音频", "转写", "审阅", "完成" };
        for (var i = 0; i < labels.Length; i++)
        {
            var label = WorkspaceUi.Text(labels[i], 12, i == (int)(_vm.Project?.Step ?? 0)); label.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 3 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            if (i <= _steps.Value) label.Foreground = WorkspaceUi.Accent; Grid.SetColumn(label, i); _stepLabels.Children.Add(label);
        }
        RefreshProjects(); BuildStage();
    }
    private void BuildStage()
    {
        var key = $"{_vm.Project?.Id}|{_vm.Project?.Step}|{_vm.Project?.ReviewPhase}|{_overview}|{_manual}";
        _stage.AnimateChanges = key != _stageKey; _stageKey = key;
        if (_vm.Project is not { } p)
        {
            _reviewList = null; _reviewScroll = null; _reviewStructure = null; _cueItemIndexes.Clear();
            var empty = WorkspaceUi.Stack(WorkspaceUi.Icon("open_folder_horizontal", 40), WorkspaceUi.Text("还没有课程项目", 17, true), WorkspaceUi.Text("创建项目后即可绑定音频、转写字幕并导出精听包。"));
            empty.Children[0].Margin = new Thickness(0, 0, 0, 10);
            foreach (var text in empty.Children.OfType<TextBlock>()) text.TextAlignment = TextAlignment.Center;
            empty.MaxWidth = 360; empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; _stage.Content = empty; return;
        }
        if (!p.HasAudio)
        {
            _stage.Content = StepSurface("选择音频", "音频会复制到项目目录，之后可以随时退出并恢复制作。", WorkspaceUi.Button("选择音频", () => RunAsync(BindAudioAsync), true)); return;
        }
        if (p.Step == CourseProjectStep.Transcription && !p.HasTranscript)
        {
            var job = p.TranscriptionJobId is string id ? _queue.JobById(id) : null;
            var actions = WorkspaceUi.Button(job?.Status == TranscriptionJobStatus.Failed ? "重新转写" : "开始转写", () => RunAsync(TranscribeAsync), true); actions.IsEnabled = job?.IsActive != true;
            var replace = WorkspaceUi.Button("更换音频", () => RunAsync(BindAudioAsync)); replace.IsEnabled = job?.Status != TranscriptionJobStatus.Running;
            var body = WorkspaceUi.Stack(); if (job != null) body.Children.Add(WorkspaceUi.Text(job.Message));
            if (job?.Status == TranscriptionJobStatus.Running) body.Children.Add(WorkspaceUi.Row(new ProgressBar { Value = (job.Fraction ?? 0) * 100, Width = 520, Height = 4 }, WorkspaceUi.Text($"{Math.Round((job.Fraction ?? 0) * 100)}%")));
            body.Children.Add(WorkspaceUi.Row(actions, WorkspaceUi.Button("导入 SRT", () => RunAsync(ImportSrtAsync)), replace));
            _stage.Content = StepSurface("转写", Path.GetFileName(p.AudioPath!), body); return;
        }
        _stage.Content = BuildReview(p);
    }
    private static Control StepSurface(string title, string description, Control child)
    {
        child.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Spacing = 4, Children = { WorkspaceUi.Text(title, 20), WorkspaceUi.Text(description, 12), new Border { Margin = new Thickness(0, 14, 0, 0), Child = child } } };
    }
    private async Task CloseProjectAsync() { await SaveDraftAsync(); await _vm.OpenAsync(""); RenderProject(); }
    private Control BuildReview(CourseProject p)
    {
        var grouping = p.ReviewPhase == ReviewPhase.Grouping;
        var structure = SrtTranscriptStructure.FromCues(_editingCues, !_manual);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(WorkspaceUi.Text(p.Step == CourseProjectStep.Completed ? "编辑练习" : "审阅与练习", 20));
        var phase = WorkspaceUi.Text(grouping ? "分题" : "设置挖空", 14, true); phase.Foreground = WorkspaceUi.Accent; heading.Children.Add(phase);
        if (grouping) heading.Children.Add(WorkspaceUi.Button(_manual ? "恢复自动处理" : "手动操作", () => { _manual = !_manual; _overview = false; BuildStage(); return Task.CompletedTask; }));
        toolbar.Children.Add(heading);
        if (grouping)
        {
            var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            commands.Children.Add(WorkspaceUi.Button(_overview ? "返回原文" : $"题目总览 ({p.Exercises.Questions.Count})", () => { _overview = !_overview; BuildStage(); return Task.CompletedTask; }));
            _createQuestion = WorkspaceUi.Button($"新建题目 ({_selected.Count})", () => RunAsync(async () => { await SaveExercisesAsync(TeacherWorkspaceViewModel.CreateMaterial(_vm.Project!.Exercises, _selected)); _selected.Clear(); BuildStage(); }), true);
            _createQuestion.IsEnabled = !_overview && _selected.Count > 0; commands.Children.Add(_createQuestion); Grid.SetColumn(commands, 1); toolbar.Children.Add(commands);
        }
        layout.Children.Add(toolbar);
        var repeats = p.Exercises.EffectiveMaterials.Count(m => m.RepeatedCueIndexes.Count > 0);
        var description = WorkspaceUi.Text(!grouping ? "点击英文单词设置挖空；标点不参与分词。" : _overview ? "集中编辑题号、题目内容与题目顺序。" : _manual ? "当前显示全部 SRT 内容，可直接编辑并手动选择连续字幕。" : p.Exercises.Questions.Count > 0 ? $"已整理 {p.Exercises.EffectiveMaterials.Count} 段材料、{p.Exercises.Questions.Count} 道题；{repeats} 段包含重复朗读。" : structure.HasMarkers ? $"进入页面时已自动识别并隔离 {structure.MarkerCueIndexes.Count} 条提示，形成 {structure.Sections.Count} 个可选区段。" : "未检测到提示句，按连续原文显示；拖动选择连续字幕后新建题目。", 12);
        description.Margin = new Thickness(0, 4, 0, 10); Grid.SetRow(description, 1); layout.Children.Add(description);
        Control body = _editingCues.Count == 0 ? new TextBlock { Text = "字幕格式有误，无法生成时间卡片。", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : grouping && _overview ? BuildQuestionOverview(p, structure) : BuildTranscript(p, structure, grouping);
        Grid.SetRow(body, 2); layout.Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        if (!grouping) footer.Children.Add(BackToQuestionsButton());
        Control next = grouping ? WorkspaceUi.Button("下一步：设置挖空", () => RunAsync(() => ChangePhaseAsync(ReviewPhase.Cloze)), true)
            : p.Step != CourseProjectStep.Completed ? WorkspaceUi.Button("完成审阅", () => RunAsync(FinishReviewAsync), true)
            : BuildDeliveryCommands();
        Grid.SetColumn(next, 1); footer.Children.Add(next); Grid.SetRow(footer, 3); layout.Children.Add(footer);
        return layout;
    }
    private Control BackToQuestionsButton()
    {
        var button = WorkspaceUi.Button("返回题目", () => RunAsync(() => ChangePhaseAsync(ReviewPhase.Grouping)));
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; label.Children.Add(WorkspaceUi.Icon("back")); label.Children.Add(WorkspaceUi.Text("返回题目")); button.Content = label;
        button.HorizontalAlignment = HorizontalAlignment.Left; return button;
    }
    private Control BuildDeliveryCommands()
    {
        var export = WorkspaceUi.Button(_exporting ? "正在导出 .ilp" : "导出为精听包", () => RunAsync(() => DeliverAsync(false)), true);
        var add = WorkspaceUi.Button(_addingToLibrary ? "正在添加" : "添加到播放", () => RunAsync(() => DeliverAsync(true)));
        export.IsEnabled = add.IsEnabled = !_exporting && !_addingToLibrary;
        var commands = new WrapPanel(); export.Margin = new Thickness(0, 0, 8, 0); commands.Children.Add(export); commands.Children.Add(add); return commands;
    }
    private async Task DeliverAsync(bool addToLibrary)
    {
        _addingToLibrary = addToLibrary; _exporting = !addToLibrary; BuildStage();
        try { if (addToLibrary) await AddToLibraryAsync(); else await ExportIlpAsync(); }
        finally { _addingToLibrary = _exporting = false; BuildStage(); }
    }
    private async Task ChangePhaseAsync(ReviewPhase phase) { await SaveDraftAsync(); if (_vm.Project is { } p) { await _vm.SaveAsync(p with { ReviewPhase = phase, AutomaticQuestionPlanApplied = true }); RenderProject(); } }
    private static string Clock(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    private string Range(LessonMaterial material) => material.CueIndexes.Count > 0 && material.CueIndexes.All(i => i >= 0 && i < _editingCues.Count) ? $"{Clock(_editingCues[material.CueIndexes.Min()].Start)} – {Clock(_editingCues[material.CueIndexes.Max()].End)}" : "";
    private static readonly Color[] MaterialColors = [Color.Parse("#2563EB"), Color.Parse("#7C3AED"), Color.Parse("#0F766E"), Color.Parse("#B45309"), Color.Parse("#BE185D"), Color.Parse("#3F6212")];
    private Color FrameColor(CourseProject p, LessonMaterial? material)
    {
        if (material == null) return Color.Parse("#8A8A8A");
        var index = p.Exercises.EffectiveMaterials.ToList().FindIndex(m => m.Id == material.Id); return MaterialColors[Math.Max(0, index) % MaterialColors.Length];
    }
    private static string MaterialLabel(LessonExercises exercises, LessonMaterial? material) => material == null ? "未归题" : exercises.QuestionsForMaterial(material) is var questions && questions.Count > 0 ? "第 " + string.Join('、', questions.Select(q => q.Number)) + " 题" : "听力材料";
    private sealed record TranscriptRow(Func<Control> Build, int? CueIndex = null);
    private static readonly ControlTheme TranscriptItemTheme = new(typeof(ListBoxItem))
    {
        Setters =
        {
            new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ListBoxItem>((item, _) => new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 4),
                [!ContentPresenter.ContentProperty] = item[!ContentControl.ContentProperty],
                [!ContentPresenter.ContentTemplateProperty] = item[!ContentControl.ContentTemplateProperty]
            })),
            new Setter(TemplatedControl.FocusableProperty, false)
        }
    };
    private Control BuildTranscript(CourseProject p, SrtTranscriptStructure structure, bool grouping)
    {
        var previousOffset = _reviewScroll?.Offset ?? default;
        _cueRows.Clear(); _cueItemIndexes.Clear(); _reviewStructure = structure;
        var rows = new List<TranscriptRow>();
        var transcriptSnapshot = _editingCues;
        var materials = p.Exercises.EffectiveMaterials;
        var materialByCue = new Dictionary<int, LessonMaterial>();
        foreach (var material in materials)
            foreach (var cue in material.CueIndexes) materialByCue.TryAdd(cue, material);
        void AddCue(int cue)
        {
            _cueItemIndexes[cue] = rows.Count;
            rows.Add(new TranscriptRow(() => BuildCueRow(p, cue, grouping, transcriptSnapshot[cue]), cue));
        }
        for (var sectionIndex = 0; sectionIndex < structure.Sections.Count; sectionIndex++)
        {
            var section = structure.Sections[sectionIndex];
            rows.Add(new TranscriptRow(() =>
            {
                var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(2, 10, 2, 7) };
                var label = WorkspaceUi.Text(section.Label, 14, true); label.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(label);
                var count = WorkspaceUi.Text($"{section.CueIndexes.Count} 句", 12); count.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(count, 1); header.Children.Add(count);
                if (grouping)
                {
                    var choose = WorkspaceUi.Button("选择整段", () => { SelectSection(section); BuildStage(); return Task.CompletedTask; }); choose.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(choose, 2); header.Children.Add(choose);
                }
                return header;
            }));
            var indexes = section.CueIndexes.ToArray(); var start = 0;
            while (start < indexes.Length)
            {
                var material = materialByCue.GetValueOrDefault(indexes[start]); var end = start + 1;
                while (end < indexes.Length && materialByCue.GetValueOrDefault(indexes[end])?.Id == material?.Id) end++;
                var run = indexes[start..end];
                if (grouping)
                {
                    rows.Add(new TranscriptRow(() =>
                    {
                        var color = FrameColor(p, material); var draft = material == null && _selected.Contains(run[0]); if (draft) color = Color.Parse("#B80018");
                        var group = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                        group.Children.Add(WorkspaceUi.Text(draft ? "待新建题目" : MaterialLabel(p.Exercises, material), 14, true)); var size = WorkspaceUi.Text($"{run.Length} 句", 12); Grid.SetColumn(size, 1); group.Children.Add(size);
                        return new Border { Child = group, Padding = new Thickness(12, 8), Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(4, 0, 0, 0), BorderBrush = new SolidColorBrush(color), Background = new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B)) };
                    }));
                }
                var repeated = !_manual && material != null ? run.Where(material.RepeatedCueIndexes.Contains).ToArray() : [];
                foreach (var cue in run.Except(repeated)) AddCue(cue);
                if (repeated.Length > 0)
                {
                    var repeatId = $"{sectionIndex}-{material!.Id}-{start}"; var expanded = _expandedRepeats.Contains(repeatId);
                    rows.Add(new TranscriptRow(() =>
                    {
                        var repeat = WorkspaceUi.Button("重复朗读", () => { if (!_expandedRepeats.Add(repeatId)) _expandedRepeats.Remove(repeatId); BuildStage(); return Task.CompletedTask; });
                        var repeatLabel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,*,Auto") }; repeatLabel.Children.Add(WorkspaceUi.Icon(expanded ? "chevron_down" : "chevron_right", 14)); var repeatTitle = WorkspaceUi.Text("重复朗读"); Grid.SetColumn(repeatTitle, 2); repeatLabel.Children.Add(repeatTitle); var repeatCount = WorkspaceUi.Text($"{repeated.Length} 句"); Grid.SetColumn(repeatCount, 3); repeatLabel.Children.Add(repeatCount); repeat.Content = repeatLabel;
                        repeat.HorizontalAlignment = HorizontalAlignment.Stretch; repeat.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                        return repeat;
                    }));
                    if (expanded) foreach (var cue in repeated) AddCue(cue);
                }
                start = end;
            }
        }
        // Keep the viewport bounded: only realized rows create editors and cloze buttons.
        var list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            ItemContainerTheme = TranscriptItemTheme,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<TranscriptRow>((entry, _) => entry?.Build(), supportsRecycling: false),
            ItemsSource = rows
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        _reviewList = list; _reviewScroll = null;
        list.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_reviewList != list) return;
            _reviewScroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_reviewScroll != null) _reviewScroll.Offset = previousOffset;
        }, DispatcherPriority.Loaded);
        list.AddHandler(PointerReleasedEvent, (_, _) => { if (_dragSelection != null) { _dragSelection = null; _dragAnchor = _dragSection = null; BuildStage(); } }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        return list;
    }
    private Control BuildCueRow(CourseProject p, int index, bool grouping, SrtCue cue)
    {
        var material = p.Exercises.MaterialForCue(index);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(grouping ? "72,38,*" : "72,*"), MinHeight = 54, Margin = new Thickness(10, 8) };
        var metadata = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center }; metadata.Children.Add(WorkspaceUi.Text(Clock(cue.Start), 12)); metadata.Children.Add(WorkspaceUi.Text(MaterialLabel(p.Exercises, material), 12)); row.Children.Add(metadata);
        if (grouping)
        {
            var handle = new Border { Width = 30, Height = 34, VerticalAlignment = VerticalAlignment.Center,
                Child = material != null ? WorkspaceUi.Icon("lock", 14) : new CheckBox { IsChecked = _selected.Contains(index), IsHitTestVisible = false, MinWidth = 20, MinHeight = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            ToolTip.SetTip(handle, material != null ? "已归入题目" : "选择字幕");
            handle.PointerPressed += (_, e) => { if (material == null) { _dragAnchor = index; _dragSection = _reviewStructure?.SectionIndexForCue(index); _dragSelection = !_selected.Contains(index); ApplyDragSelection(index); e.Handled = true; } };
            row.PointerEntered += (_, _) => { if (_dragSelection != null && material == null) ApplyDragSelection(index); };
            Grid.SetColumn(handle, 1); row.Children.Add(handle);
            var text = WorkspaceUi.Input(cue.Text, multiline: true); text.MinHeight = 32; text.MaxHeight = 84; text.VerticalAlignment = VerticalAlignment.Center;
            text.TextChanged += (_, _) => { if (!_loading && _vm.Project?.Id == p.Id && index < _editingCues.Count && text.Text != _editingCues[index].Text) { var cues = _editingCues.ToArray(); cues[index] = cues[index] with { Text = text.Text ?? "" }; _editingCues = cues; _transcript.Text = SrtParser.Serialize(cues); } };
            Grid.SetColumn(text, 2); row.Children.Add(text);
        }
        else
        {
            var words = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var part in LessonTextTokenizer.Tokenize(cue.Text))
            {
                if (!part.IsWord) { var punctuation = WorkspaceUi.Text(part.Text); punctuation.VerticalAlignment = VerticalAlignment.Center; words.Children.Add(punctuation); continue; }
                var word = part.WordIndex!.Value; var selected = p.Exercises.ClozeWordIndexes.TryGetValue(index, out var blanks) && blanks.Contains(word);
                var button = new ToggleButton { Content = part.Text, IsChecked = selected, Margin = new Thickness(0, 0, 2, 6), Padding = new Thickness(10, 4) };
                button.Click += async (_, _) => await RunAsync(async () => { await SaveDraftAsync(); if (_vm.Project is {} current) await SaveExercisesAsync(current.Exercises with { ClozeWordIndexes = ClozeSync.Toggle(current.Exercises.ClozeWordIndexes, _editingCues, current.Exercises, index, word) }); });
                words.Children.Add(button);
            }
            Grid.SetColumn(words, 1); row.Children.Add(words);
        }
        var color = FrameColor(p, material); var frame = new Border { Child = row, BorderBrush = new SolidColorBrush(color), BorderThickness = grouping ? new Thickness(2, 0, 0, 0) : new Thickness(0), Background = grouping ? new SolidColorBrush(Color.FromArgb(6, color.R, color.G, color.B)) : null, CornerRadius = grouping ? new CornerRadius(0) : new CornerRadius(4) };
        if (!grouping) frame.Bind(Border.BackgroundProperty, new DynamicResourceExtension("CardBackgroundFillColorDefaultBrush"));
        frame.AttachedToVisualTree += (_, _) => _cueRows[index] = frame;
        frame.DetachedFromVisualTree += (_, _) => { if (_cueRows.GetValueOrDefault(index) == frame) _cueRows.Remove(index); };
        return frame;
    }
    private void SelectSection(SrtTranscriptSection section)
    {
        var available = section.CueIndexes.Where(i => _vm.Project?.Exercises.MaterialForCue(i) == null).ToArray();
        _selected.Clear(); foreach (var cue in available) _selected.Add(cue);
    }
    private void ApplyDragSelection(int index)
    {
        if (_dragAnchor is not int anchor || _dragSelection is not bool value || _vm.Project is not { } p) return;
        var structure = _reviewStructure;
        if (structure == null || structure.SectionIndexForCue(index) != _dragSection) return;
        foreach (var cue in Enumerable.Range(Math.Min(anchor, index), Math.Abs(index - anchor) + 1))
        {
            if (structure.SectionIndexForCue(cue) != _dragSection || p.Exercises.MaterialForCue(cue) != null) continue;
            if (value) _selected.Add(cue); else _selected.Remove(cue);
        }
        _dragAnchor = index;
        foreach (var (cue, control) in _cueRows)
        {
            if (control is Border { Child: Grid row } && row.Children.OfType<Border>().FirstOrDefault() is {} handle)
            {
                if (handle.Child is CheckBox selection) selection.IsChecked = _selected.Contains(cue);
            }
        }
        if (_createQuestion != null) { _createQuestion.Content = $"新建题目 ({_selected.Count})"; _createQuestion.IsEnabled = !_overview && _selected.Count > 0; }
    }
    private readonly Dictionary<string, LessonQuestion> _questionEdits = [];
    private Control BuildQuestionOverview(CourseProject p, SrtTranscriptStructure structure)
    {
        if (p.Exercises.EffectiveMaterials.Count == 0)
        {
            var empty = WorkspaceUi.Stack(WorkspaceUi.Icon("edit_create", 30), WorkspaceUi.Text("还没有题目", 14, true), WorkspaceUi.Text("返回原文，选择连续字幕后创建题目。", 12), WorkspaceUi.Button("返回原文", () => { _overview = false; BuildStage(); return Task.CompletedTask; }));
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; return empty;
        }
        var materials = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var material in p.Exercises.EffectiveMaterials)
        {
            var questions = p.Exercises.QuestionsForMaterial(material);
            var panel = new StackPanel(); var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(14, 12, 8, 10) };
            var note = WorkspaceUi.Icon("music_note", 17); note.VerticalAlignment = VerticalAlignment.Center; note.Margin = new Thickness(0, 0, 10, 0); heading.Children.Add(note);
            var identity = WorkspaceUi.Stack(WorkspaceUi.Text(MaterialLabel(p.Exercises, material), 14, true), WorkspaceUi.Text($"{Range(material)}  ·  {material.CueIndexes.Count} 句  ·  {questions.Count} 小题", 12)); identity.Spacing = 3; Grid.SetColumn(identity, 1); heading.Children.Add(identity);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(WorkspaceUi.Button("添加小题", () => RunAsync(() => SaveExercisesAsync(TeacherWorkspaceViewModel.AddQuestion(_vm.Project!.Exercises, material.Id)))));
            var leadIn = WorkspaceUi.Button("绑定所选提前提示", () => RunAsync(() => UpdateLeadInAsync(material.Id, _selected.ToArray()))); leadIn.IsEnabled = _selected.Count > 0; actions.Children.Add(leadIn);
            if (material.LeadInCueIndexes.Count > 0) actions.Children.Add(WorkspaceUi.Button("清除提示", () => RunAsync(() => UpdateLeadInAsync(material.Id, []))));
            var locate = WorkspaceUi.IconButton("search", "在原文中查看", () => LocateMaterialAsync(material)); locate.Classes.Add("subtle"); ToolTip.SetTip(locate, "在原文中查看"); actions.Children.Add(locate); Grid.SetColumn(actions, 2); heading.Children.Add(actions);
            panel.Children.Add(heading); panel.Children.Add(new Separator());
            foreach (var question in questions)
            {
                if (question != questions[0]) panel.Children.Add(new Separator());
                var sectionIndex = material.CueIndexes.Count > 0 ? structure.SectionIndexForCue(material.CueIndexes[0]) : null;
                panel.Children.Add(BuildQuestionEditor(question, material, sectionIndex is int si ? structure.Sections[si].Label : "原文"));
            }
            materials.Children.Add(WorkspaceUi.Surface(panel, new Thickness(0)));
        }
        return new ScrollViewer { Content = materials, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private Control BuildQuestionEditor(LessonQuestion question, LessonMaterial material, string section)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("104,12,*,12,Auto"), Margin = new Thickness(14, 10, 8, 12) };
        var number = new NumericUpDown { Value = question.Number, Minimum = 1, Maximum = 999, Increment = 1, FormatString = "0" };
        row.Children.Add(WorkspaceUi.Stack(WorkspaceUi.Text("题号", 12), number));
        var title = WorkspaceUi.Input(question.Title, "输入题目内容", true); title.MinHeight = 60; title.MaxHeight = 160;
        var body = new StackPanel { Spacing = 5 }; body.Children.Add(WorkspaceUi.Text("题目", 12)); body.Children.Add(title); body.Children.Add(new TextBlock { Text = "选项与答案", FontSize = 12, Margin = new Thickness(0, 5, 0, 0) });
        var optionInputs = new List<TextBox>(); var answerBoxes = new List<CheckBox>();
        foreach (var (option, index) in question.Options.Select((o, i) => (o, i)))
        {
            var optionRow = new Grid { ColumnDefinitions = new ColumnDefinitions("25,*,6,Auto,Auto"), Margin = new Thickness(0, 0, 0, 1) };
            var letter = WorkspaceUi.Text($"{(char)('A' + index)}."); letter.VerticalAlignment = VerticalAlignment.Center; optionRow.Children.Add(letter);
            var input = WorkspaceUi.Input(option, "设置选项"); optionInputs.Add(input); Grid.SetColumn(input, 1); optionRow.Children.Add(input);
            var answer = new CheckBox { Content = "答案", IsChecked = question.AnswerIndex == index, VerticalAlignment = VerticalAlignment.Center }; answerBoxes.Add(answer); ToolTip.SetTip(answer, "设为正确答案"); Grid.SetColumn(answer, 3); optionRow.Children.Add(answer);
            var remove = WorkspaceUi.IconButton("delete", "删除选项", () => RunAsync(async () =>
            {
                await SaveDraftAsync(); var current = _vm.Project!.Exercises.Questions.First(q => q.Id == question.Id);
                await SaveExercisesAsync(TeacherWorkspaceViewModel.UpdateQuestion(_vm.Project.Exercises, current with { Options = current.Options.Where((_, i) => i != index).ToArray(), AnswerIndex = current.AnswerIndex == index ? null : current.AnswerIndex > index ? current.AnswerIndex - 1 : current.AnswerIndex }));
            })); remove.Padding = new Thickness(6); ToolTip.SetTip(remove, "删除选项"); Grid.SetColumn(remove, 4); optionRow.Children.Add(remove); body.Children.Add(optionRow);
        }
        LessonQuestion Read() => (_questionEdits.GetValueOrDefault(question.Id) ?? question) with { Title = title.Text ?? "", Options = optionInputs.Select(i => i.Text ?? "").ToArray(), AnswerIndex = answerBoxes.FindIndex(a => a.IsChecked == true) is var answer && answer >= 0 ? answer : null, Number = (int)(number.Value ?? 1) };
        void QueueEdit() { _questionEdits[question.Id] = Read(); ScheduleSave(); }
        title.TextChanged += (_, _) => QueueEdit(); number.ValueChanged += (_, _) => QueueEdit(); foreach (var input in optionInputs) input.TextChanged += (_, _) => QueueEdit();
        foreach (var answer in answerBoxes) answer.IsCheckedChanged += (_, _) => { if (_loading) return; _loading = true; if (answer.IsChecked == true) foreach (var other in answerBoxes.Where(a => a != answer)) other.IsChecked = false; _loading = false; QueueEdit(); };
        body.Children.Add(WorkspaceUi.Button("添加选项", () => RunAsync(async () =>
        {
            await SaveDraftAsync(); var current = _vm.Project!.Exercises.Questions.First(q => q.Id == question.Id);
            await SaveExercisesAsync(TeacherWorkspaceViewModel.UpdateQuestion(_vm.Project.Exercises, current with { Options = current.Options.Append("").ToArray() }));
        })));
        body.Children.Add(new TextBlock { Text = $"{section}  ·  {Range(material)}  ·  {question.CueIndexes.Count} 句", FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(body, 2); row.Children.Add(body);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var locate = WorkspaceUi.IconButton("search", "在原文中查看", () => LocateMaterialAsync(material)); locate.Classes.Add("subtle"); ToolTip.SetTip(locate, "在原文中查看"); actions.Children.Add(locate);
        var delete = WorkspaceUi.IconButton("delete", "删除题目", () => RunAsync(async () => { await SaveDraftAsync(); await SaveExercisesAsync(TeacherWorkspaceViewModel.RemoveQuestion(_vm.Project!.Exercises, question.Id)); })); ToolTip.SetTip(delete, "删除题目"); actions.Children.Add(delete);
        Grid.SetColumn(actions, 4); row.Children.Add(actions); return row;
    }
    private async Task UpdateLeadInAsync(string id, int[] indexes)
    {
        await SaveDraftAsync(); if (_vm.Project is not {} p) return;
        var material = p.Exercises.EffectiveMaterials.First(m => m.Id == id);
        await SaveExercisesAsync(TeacherWorkspaceViewModel.UpdateMaterial(p.Exercises, material with { LeadInCueIndexes = indexes }, _editingCues.Count)); _selected.Clear(); BuildStage();
    }
    private Task LocateMaterialAsync(LessonMaterial material)
    {
        _overview = false; BuildStage();
        if (material.CueIndexes.Count > 0 && _cueItemIndexes.TryGetValue(material.CueIndexes[0], out var index) && _reviewList is {} list)
            Dispatcher.UIThread.Post(() => { if (_reviewList == list) list.ScrollIntoView(index); }, DispatcherPriority.Loaded);
        return Task.CompletedTask;
    }
    private void ScheduleSave() { if (_loading || _agentMode || _vm.Project == null) return; _autosave.Stop(); _autosave.Start(); }
    private async Task SaveDraftAsync()
    {
        _autosave.Stop(); if (_vm.Project == null || _loading || _agentMode || _saving) return;
        _saving = true;
        try
        {
            var p = _vm.Project; var text = _transcript.Text ?? ""; var changed = p.Transcript != text;
            var title = string.IsNullOrWhiteSpace(_title.Text) ? "未命名项目" : _title.Text.Trim();
            if (!changed && title == p.Title && _questionEdits.Count == 0) return;
            if (changed) RetireTranscription(p);
            var exercises = p.Exercises;
            var edits = _questionEdits.ToArray();
            var renumbered = edits.Any(e => p.Exercises.Questions.FirstOrDefault(q => q.Id == e.Key)?.Number != e.Value.Number);
            foreach (var question in edits.Select(e => e.Value)) if (exercises.Questions.Any(q => q.Id == question.Id)) exercises = TeacherWorkspaceViewModel.UpdateQuestion(exercises, question);
            await _vm.SaveAsync(p with { Title = title, Transcript = text, Exercises = exercises, TranscriptionJobId = changed ? null : p.TranscriptionJobId,
                Step = p.Step == CourseProjectStep.Completed ? p.Step : string.IsNullOrWhiteSpace(text) ? p.HasAudio ? CourseProjectStep.Transcription : CourseProjectStep.Audio : CourseProjectStep.Review });
            foreach (var edit in edits) if (_questionEdits.TryGetValue(edit.Key, out var current) && current == edit.Value) _questionEdits.Remove(edit.Key);
            RefreshProjects(); if (renumbered) BuildStage();
        }
        finally { _saving = false; }
    }
    private void RetireTranscription(CourseProject p) { if (p.TranscriptionJobId is string id) { _queue.Cancel(id); _queue.MarkSrtConsumed(id); } }
    private async Task SaveExercisesAsync(LessonExercises exercises)
    {
        var edits = _questionEdits.Values.ToArray();
        await SaveDraftAsync(); if (_vm.Project == null) return;
        foreach (var edit in edits) if (exercises.Questions.Any(q => q.Id == edit.Id)) exercises = TeacherWorkspaceViewModel.UpdateQuestion(exercises, edit);
        await _vm.SaveAsync(_vm.Project with { Exercises = exercises, AutomaticQuestionPlanApplied = true }); RenderProject();
    }
    public Task CreateProjectAsync() => RunAsync(NewAsync);
    public async Task PickAudioAndEnqueueAsync() => await RunAsync(async () =>
    {
        var audio = await WorkspaceUi.Pick(this, "选择转写音频", "mp3", "wav", "m4a"); if (audio == null) return;
        await SaveDraftAsync(); var project = await _store.CreateAsync(audio);
        project = project with { AudioDuration = await VadSlicer.ProbeDurationAsync(project.AudioPath!) }; await _store.SaveAsync(project);
        await _vm.OpenAsync(project.Id); RenderProject(); await TranscribeAsync();
    });
    private async Task NewAsync()
    {
        var choice = await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this), "新建项目", "可以先创建空白项目，也可以选择音频并直接进入转写步骤。", "空白项目", "选择音频");
        if (choice < 0) return; var audio = choice == 1 ? await WorkspaceUi.Pick(this, "选择课程音频", "mp3", "wav", "m4a") : null;
        if (choice == 1 && audio == null) return; await SaveDraftAsync(); var p = await _store.CreateAsync(audio); if (p.AudioPath != null) { p = p with { AudioDuration = await VadSlicer.ProbeDurationAsync(p.AudioPath) }; await _store.SaveAsync(p); } await _vm.OpenAsync(p.Id); RenderProject();
    }
    private async Task BindAudioAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Pick(this, "绑定课程音频", "mp3", "wav", "m4a"); if (path == null) return; RetireTranscription(_vm.Project); await _vm.SaveAsync(await _store.BindAudioAsync(_vm.Project, path, await VadSlicer.ProbeDurationAsync(path))); RenderProject(); }
    private async Task ImportSrtAsync()
    {
        await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Pick(this, "导入字幕", "srt"); if (path == null) return;
        var text = await File.ReadAllTextAsync(path); var cues = SrtParser.Parse(Encoding.UTF8.GetBytes(text), _vm.Project.AudioDuration ?? TimeSpan.FromDays(7));
        var preserve = _vm.Cues.Count == cues.Count && _vm.Cues.Zip(cues).All(pair => pair.First.Start == pair.Second.Start && pair.First.End == pair.Second.End);
        RetireTranscription(_vm.Project); await _vm.SaveAsync(_vm.Project with { Transcript = text, Step = CourseProjectStep.Review, ReviewPhase = ReviewPhase.Grouping, TranscriptionJobId = null,
            Exercises = preserve ? _vm.Project.Exercises : SrtQuestionPlanner.Plan(cues), AutomaticQuestionPlanApplied = true, AutoQuestionPlanDeferred = false }); RenderProject(); WorkspaceToast.Show(this, "字幕已导入\n现在可以进行题目与挖空审阅。");
    }
    private async Task TranscribeAsync()
    {
        await SaveDraftAsync(); if (_vm.Project?.AudioPath == null) throw new InvalidOperationException("请先绑定音频。");
        if (!_settings().CloudReady) { _openSettings(); return; }
        var id = _queue.Enqueue(_vm.Project.Title, _vm.Project.AudioPath, _vm.Project.AudioDuration, _vm.Project.Id);
        await _vm.SaveAsync(_vm.Project with { TranscriptionJobId = id, Step = CourseProjectStep.Transcription, AutoQuestionPlanDeferred = false }); RenderProject(); WorkspaceToast.Show(this, "已加入转写队列\n可以继续使用应用，并从任务中心查看进度。");
    }
    private async Task ImportArchiveAsync()
    {
        var path = await WorkspaceUi.Pick(this, "导入制作工程", "zip"); if (path == null) return;
        await SaveDraftAsync(); await WithBusyAsync("正在解压并导入工程…", async () => { var project = await _store.ImportZipAsync(path); await _vm.OpenAsync(project.Id); RenderProject(); WorkspaceToast.Show(this, "工程已导入\n音频、字幕和全部制作数据已恢复。"); });
    }
    private async Task ExportArchiveAsync()
    {
        await SaveDraftAsync(); if (_vm.Project == null) return;
        var path = await WorkspaceUi.Save(this, "导出制作工程", _vm.Project.Title + "-工程.zip", "zip");
        if (path != null) await WithBusyAsync("正在打包并导出工程…", async () => { await _store.ExportZipToFileAsync(_vm.Project, path); WorkspaceToast.Show(this, "工程已导出\nZIP 包含音频、字幕和全部制作数据。"); });
    }
    private async Task ImportExamAsync()
    {
        await SaveDraftAsync(); if (_vm.Project == null) return;
        var path = await WorkspaceUi.Pick(this, "导入 DOCX 试卷", "docx"); if (path == null) return;
        await WithBusyAsync("正在解析 DOCX 试卷…", async () => { var document = await _store.ImportExamDocumentAsync(_vm.Project, path); await _vm.OpenAsync(document.Project.Id); RenderProject(); WorkspaceToast.Show(this, $"试卷已导入\n已提取 {document.ParagraphCount} 个段落和 {document.TableCount} 个表格。"); });
    }
    private async Task ExportSrtAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Save(this, "导出课程字幕", _vm.Project.Title + ".srt", "srt"); if (path != null) await File.WriteAllTextAsync(path, _vm.Project.Transcript); }
    private async Task ExportExamAsync() { if (_vm.Project == null) return; var document = await _store.ReadExamDocumentAsync(_vm.Project); if (document == null) throw new InvalidOperationException("请先导入试卷。"); var path = await WorkspaceUi.Save(this, "导出试卷文本", _vm.Project.Title + "-试卷.txt", "txt"); if (path != null) await File.WriteAllTextAsync(path, document.Text); }
    private async Task FinishReviewAsync() { await SaveDraftAsync(); if (_vm.Project == null || !_vm.Project.HasTranscript) return; SrtParser.Parse(Encoding.UTF8.GetBytes(_vm.Project.Transcript), _vm.Project.AudioDuration ?? TimeSpan.FromDays(7)); await _vm.SaveAsync(_vm.Project with { Step = CourseProjectStep.Completed }); RenderProject(); WorkspaceToast.Show(this, "审阅完成\n现在可以加入播放或导出课程。"); }
    private async Task ExportIlpAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Save(this, "导出精听包", _vm.Project.Title + ".ilp", "ilp"); if (path == null) return; await WithBusyAsync("正在生成精听包…", () => new ProjectDelivery().CreateIlpAsync(_vm.Project, path)); await _vm.SaveAsync(_vm.Project with { PackageVersion = _vm.Project.PackageVersion + 1, LastExportPath = path, Step = CourseProjectStep.Completed }); RenderProject(); WorkspaceToast.Show(this, $"课程已导出\n版本 {_vm.Project.PackageVersion} 已保存为精听包。"); }
    private async Task AddToLibraryAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; IL.Core.Projects.ProjectDeliveryResult? delivery = null; await WithBusyAsync("正在添加到播放…", async () => { delivery = await new ProjectDelivery().AddToLibraryAsync(_vm.Project, _libraryDirectory); }); await _vm.SaveAsync(_vm.Project with { PackageVersion = delivery!.PackageVersion, Step = CourseProjectStep.Completed }); RenderProject(); _libraryChanged(); WorkspaceToast.Show(this, "课程已加入播放库。"); }
    public Func<CourseProject, Task<string?>>? StandaloneExporter { get; set; }
    private async Task ExportStandaloneAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; if (StandaloneExporter == null) throw new InvalidOperationException("独立播放器导出服务尚未连接。"); var project=_vm.Project;var output=await StandaloneExporter(project);if(output is not null){await _vm.SaveAsync(project with{PackageVersion=project.PackageVersion+1,LastExportPath=output,Step=CourseProjectStep.Completed});RenderProject();} }
    private async Task DeleteProjectAsync(CourseProject project)
    {
        if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "删除项目", $"确认删除“{project.Title}”？项目音频、字幕与制作数据将一起删除。", "删除")) return;
        RetireTranscription(project); await _store.DeleteAsync(project.Id); if (_vm.Project?.Id == project.Id) await _vm.OpenAsync(""); await RefreshProjectsFromStoreAsync(); RenderProject();
    }
    private async Task WithBusyAsync(string message, Func<Task> action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new FluentAvalonia.UI.Controls.FAProgressRing { Width = 20, Height = 20, IsActive = true }); row.Children.Add(WorkspaceUi.Text(message, 14, true));
        var surface = WorkspaceUi.Surface(row, new Thickness(24, 16)); surface.HorizontalAlignment = HorizontalAlignment.Center; surface.VerticalAlignment = VerticalAlignment.Center;
        _busyOverlay.Child = surface;
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark; _busyOverlay.Background = new SolidColorBrush(dark ? Color.FromArgb(170, 32, 32, 32) : Color.FromArgb(170, 245, 245, 245));
        _busyOverlay.IsVisible = true;
        try { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await action(); }
        finally { _busyOverlay.IsVisible = false; }
    }
    private async Task RunAsync(Func<Task> action)
    {
        await _operations.WaitAsync();
        try { await action(); }
        catch (Exception ex) { WorkspaceToast.Show(this, "制作工程操作失败", ex.Message, true); IL.Core.Infrastructure.AppLog.Error("制作工程操作失败", ex); }
        finally { _operations.Release(); }
    }
}
