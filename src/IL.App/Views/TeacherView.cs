using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
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
    private readonly CourseProjectStore _store;
    private readonly TranscriptionQueue _queue;
    private readonly Func<AppSettings> _settings;
    private readonly string _libraryDirectory;
    private readonly Action _libraryChanged;
    private readonly Action _openSettings;
    private readonly TeacherWorkspaceViewModel _vm;
    private readonly ListBox _projectList = new();
    private readonly TextBox _title = WorkspaceUi.Input(hint: "课程标题");
    private readonly TextBox _transcript = WorkspaceUi.Input(hint: "SRT 字幕", multiline: true);
    private readonly ListBox _cueList = new() { SelectionMode = SelectionMode.Multiple };
    private readonly StackPanel _exercises = new() { Spacing = 12 };
    private readonly StackPanel _inspector = new() { Spacing = 10 };
    private readonly TextBlock _status = WorkspaceUi.Text("新建项目或从列表打开课程。");
    private readonly TextBlock _audio = WorkspaceUi.Text("尚未绑定音频", 12);
    private readonly Grid _editor = new();
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _loading, _saving, _agentMode;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CourseProject? _agentDraft;
    private Window? _owner;
    public TeacherView(CourseProjectStore store, TranscriptionQueue queue, Func<AppSettings> settings, string libraryDirectory, Action? libraryChanged = null, Action? openSettings = null)
    {
        _store = store; _queue = queue; _settings = settings; _libraryDirectory = libraryDirectory;
        _libraryChanged = libraryChanged ?? (() => { }); _openSettings = openSettings ?? (() => { }); _vm = new(store);
        Build();
        _title.TextChanged += (_, _) => ScheduleSave(); _transcript.TextChanged += (_, _) => ScheduleSave();
        _autosave.Tick += async (_, _) => { _autosave.Stop(); await RunAsync(SaveDraftAsync); };
        _projectList.SelectionChanged += async (_, _) => { if (!_loading && _projectList.SelectedItem is ListBoxItem { Tag: string id }) await OpenProjectAsync(id); };
        _cueList.SelectionChanged += (_, _) => BuildInspector();
        AttachedToVisualTree += async (_, _) => { _owner = TopLevel.GetTopLevel(this) as Window; await RunAsync(RefreshAsync); };
        DetachedFromVisualTree += async (_, _) => { _autosave.Stop(); await RunAsync(SaveDraftAsync); };
    }
    public async Task RefreshAsync()
    {
        await _vm.ReloadAsync(); RefreshProjects();
    }
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
    public void SetAgentMode(bool active) { if (active && !_agentMode && _vm.Project is {} p && (p.Title != _title.Text || p.Transcript != _transcript.Text)) _agentDraft = p with { Title = _title.Text ?? p.Title, Transcript = _transcript.Text ?? p.Transcript }; _agentMode = active; _autosave.Stop(); IsEnabled = !active; _editor.IsEnabled = !active; _status.Text = active ? "智能体正在制作课程。结束接管后刷新工程。" : "已返回用户模式。"; }
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
        await RefreshAsync(); if (id != null) { await _vm.OpenAsync(id); RenderProject(); }
    });
    public async Task LoadJobTranscriptAsync(TranscriptionJob job) => await RunAsync(async () =>
    {
        if (job.ProjectId == null || string.IsNullOrWhiteSpace(job.Srt)) return;
        var project = await _store.LoadByIdAsync(job.ProjectId); if (project == null) return;
        if (job.SrtConsumed || project.TranscriptionJobId != job.Id) { await _vm.OpenAsync(project.Id); RenderProject(); return; }
        await _vm.SaveAsync(project with { Transcript = job.Srt, TranscriptionJobId = null, Step = CourseProjectStep.Review, AutomaticQuestionPlanApplied = false, AutoQuestionPlanDeferred = false });
        _queue.MarkSrtConsumed(job.Id); await _vm.OpenAsync(project.Id); RenderProject(); _status.Text = "转写字幕已载入，请审阅题目与挖空。";
    });
    private void Build()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("220,5,*"), Margin = new Thickness(18), RowDefinitions = new RowDefinitions("*,Auto") };
        var projects = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        projects.Children.Add(WorkspaceUi.Text("制作工程", 16, true));
        var projectActions = WorkspaceUi.Row(WorkspaceUi.Button("新建", () => RunAsync(NewAsync), true), WorkspaceUi.Button("导入 ZIP", () => RunAsync(ImportArchiveAsync)));
        Grid.SetRow(projectActions, 1); projects.Children.Add(projectActions);
        Grid.SetRow(_projectList, 2); projects.Children.Add(_projectList);
        var projectFooter = WorkspaceUi.Row(WorkspaceUi.Button("刷新", () => RunAsync(RefreshAsync)), WorkspaceUi.Button("删除", () => RunAsync(DeleteAsync)));
        Grid.SetRow(projectFooter, 3); projects.Children.Add(projectFooter);
        Grid.SetColumn(projects, 0); root.Children.Add(projects);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(splitter, 1); root.Children.Add(splitter);
        _editor.RowDefinitions = new RowDefinitions("Auto,Auto,*"); _editor.Margin = new Thickness(14, 0, 0, 0);
        var header = WorkspaceUi.Stack(_title, _audio, WorkspaceUi.Row(
            WorkspaceUi.Button("绑定音频", () => RunAsync(BindAudioAsync)), WorkspaceUi.Button("导入 SRT", () => RunAsync(ImportSrtAsync)),
            WorkspaceUi.Button("导出 SRT", () => RunAsync(ExportSrtAsync)), WorkspaceUi.Button("开始转写", () => RunAsync(TranscribeAsync)), WorkspaceUi.Button("导入试卷", () => RunAsync(ImportExamAsync)),
            WorkspaceUi.Button("导出试卷文本", () => RunAsync(ExportExamAsync))));
        _editor.Children.Add(header);
        var actions = WorkspaceUi.Row(WorkspaceUi.Button("保存", () => RunAsync(SaveDraftAsync)), WorkspaceUi.Button("导出工程 ZIP", () => RunAsync(ExportArchiveAsync)),
            WorkspaceUi.Button("完成审阅", () => RunAsync(FinishReviewAsync)), WorkspaceUi.Button("加入播放", () => RunAsync(AddToLibraryAsync), true),
            WorkspaceUi.Button("导出 ILP", () => RunAsync(ExportIlpAsync)), WorkspaceUi.Button("独立播放器", () => RunAsync(ExportStandaloneAsync)));
        Grid.SetRow(actions, 1); _editor.Children.Add(actions);
        var tabs = new TabControl();
        var review = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,310") };
        review.Children.Add(_cueList); var resize = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(resize, 1); review.Children.Add(resize);
        var inspector = new ScrollViewer { Content = _inspector, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(inspector, 2); review.Children.Add(inspector);
        tabs.Items.Add(new TabItem { Header = "逐句审阅与挖空", Content = review });
        tabs.Items.Add(new TabItem { Header = "题目与材料", Content = new ScrollViewer { Content = _exercises } });
        tabs.Items.Add(new TabItem { Header = "SRT 原文", Content = _transcript });
        Grid.SetRow(tabs, 2); _editor.Children.Add(tabs); Grid.SetColumn(_editor, 2); root.Children.Add(_editor);
        Grid.SetRow(_status, 1); Grid.SetColumnSpan(_status, 3); _status.Margin = new Thickness(0, 12, 0, 0); root.Children.Add(_status);
        _editor.IsEnabled = false; Content = root;
    }
    private void RefreshProjects()
    {
        _loading = true;
        _projectList.ItemsSource = _vm.Projects.Select(p => new ListBoxItem { Tag = p.Id, Content = WorkspaceUi.Stack(WorkspaceUi.Text(p.Title, 14, true), WorkspaceUi.Text($"{p.Step} · {p.UpdatedAt.ToLocalTime():MM-dd HH:mm}", 11)) }).ToArray();
        _loading = false;
    }
    private void RenderProject()
    {
        var selectedCues = SelectedCues();
        _loading = true;
        _title.Text = _vm.Project?.Title ?? ""; _transcript.Text = _vm.Project?.Transcript ?? "";
        _audio.Text = _vm.Project?.AudioPath is string audio ? $"{Path.GetFileName(audio)} · {_vm.Project.AudioDuration}" : "尚未绑定音频";
        UpdateCues(selectedCues);
        _loading = false; _editor.IsEnabled = _vm.Project != null && !_agentMode; RefreshProjects(); BuildExercises(); BuildInspector();
    }
    private void UpdateCues(int[]? selection = null)
    {
        selection ??= SelectedCues();
        _cueList.ItemsSource = _vm.Cues.Select((cue, i) => new ListBoxItem { Tag = i, Content = WorkspaceUi.Stack(WorkspaceUi.Text($"{i + 1:000}  {SrtParser.Timestamp(cue.Start)} → {SrtParser.Timestamp(cue.End)}", 11), WorkspaceUi.Text(cue.Text, _settings().TranscriptFontSize)) }).ToArray();
        foreach (var item in _cueList.Items.OfType<ListBoxItem>().Where(item => selection.Contains((int)item.Tag!))) _cueList.SelectedItems?.Add(item);
    }
    private void ScheduleSave() { if (_loading || _agentMode || _vm.Project == null) return; _autosave.Stop(); _autosave.Start(); _status.Text = "正在编辑…"; }
    private async Task SaveDraftAsync()
    {
        _autosave.Stop(); if (_vm.Project == null || _loading || _agentMode || _saving) return;
        _saving = true;
        try
        {
            var p = _vm.Project; var text = _transcript.Text ?? "";
            var changed = p.Transcript != text;
            if (changed) RetireTranscription(p);
            await _vm.SaveAsync(p with { Title = string.IsNullOrWhiteSpace(_title.Text) ? "未命名项目" : _title.Text.Trim(), Transcript = text,
                TranscriptionJobId = changed ? null : p.TranscriptionJobId, UpdatedAt = DateTimeOffset.Now,
                Step = p.Step == CourseProjectStep.Completed ? p.Step : string.IsNullOrWhiteSpace(text) ? p.HasAudio ? CourseProjectStep.Transcription : CourseProjectStep.Audio : CourseProjectStep.Review });
            RefreshProjects(); if (changed) { UpdateCues(); BuildInspector(); } _status.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";
        }
        finally { _saving = false; }
    }
    private void RetireTranscription(CourseProject p) { if (p.TranscriptionJobId is string id) { _queue.Cancel(id); _queue.MarkSrtConsumed(id); } }
    private int[] SelectedCues() => _cueList.SelectedItems?.OfType<ListBoxItem>().Select(item => (int)item.Tag!).Order().ToArray() ?? [];
    private async Task SaveExercisesAsync(LessonExercises exercises)
    {
        await SaveDraftAsync(); if (_vm.Project == null) return;
        await _vm.SaveAsync(_vm.Project with { Exercises = exercises, AutomaticQuestionPlanApplied = true }); RenderProject();
    }
    private void BuildInspector()
    {
        _inspector.Children.Clear(); var indexes = SelectedCues();
        _inspector.Children.Add(WorkspaceUi.Text(indexes.Length == 0 ? "选择句子进行编辑" : $"已选择 {indexes.Length} 个句子", 16, true));
        if (_vm.Project == null) return;
        _inspector.Children.Add(WorkspaceUi.Button("所选句子新建材料 / 题目", () => RunAsync(() => SaveExercisesAsync(TeacherWorkspaceViewModel.CreateMaterial(_vm.Project.Exercises, SelectedCues())))));
        if (indexes.Length != 1) return;
        var index = indexes[0]; if (index < 0 || index >= _vm.Cues.Count) return; var cue = _vm.Cues[index];
        var start = WorkspaceUi.Input(SrtParser.Timestamp(cue.Start)); var end = WorkspaceUi.Input(SrtParser.Timestamp(cue.End)); var text = WorkspaceUi.Input(cue.Text, multiline: true);
        _inspector.Children.Add(WorkspaceUi.Field("开始", start)); _inspector.Children.Add(WorkspaceUi.Field("结束", end)); _inspector.Children.Add(WorkspaceUi.Field("字幕文本", text));
        _inspector.Children.Add(WorkspaceUi.Button("应用字幕修改", () => RunAsync(async () =>
        {
            var edited = _vm.Cues.ToArray(); edited[index] = new(ParseTime(start.Text), ParseTime(end.Text), text.Text ?? "");
            var srt = SrtParser.Serialize(edited); SrtParser.Parse(Encoding.UTF8.GetBytes(srt), _vm.Project.AudioDuration ?? TimeSpan.FromDays(7));
            _transcript.Text = srt; await SaveDraftAsync(); RenderProject();
        })));
        _inspector.Children.Add(WorkspaceUi.Text("点击单词切换挖空", 12, true));
        var words = new WrapPanel();
        foreach (var part in LessonTextTokenizer.Tokenize(cue.Text))
        {
            if (!part.IsWord) { words.Children.Add(WorkspaceUi.Text(part.Text)); continue; }
            var word = part.WordIndex!.Value;
            var blank = _vm.Project.Exercises.ClozeWordIndexes.TryGetValue(index, out var blanks) && blanks.Contains(word);
            words.Children.Add(WorkspaceUi.Button(blank ? $"[{part.Text}]" : part.Text, () => RunAsync(async () =>
            {
                var exercises = _vm.Project.Exercises;
                await SaveExercisesAsync(exercises with { ClozeWordIndexes = ClozeSync.Toggle(exercises.ClozeWordIndexes, _vm.Cues, exercises, index, word) });
            }), blank));
        }
        _inspector.Children.Add(words);
    }
    private void BuildExercises()
    {
        _exercises.Children.Clear(); if (_vm.Project == null) return;
        _exercises.Children.Add(WorkspaceUi.Row(WorkspaceUi.Button("自动整理题目", () => RunAsync(async () =>
        {
            if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "重新整理题目", "将根据字幕重新识别听力材料和题目，现有题目编辑将被替换。", "整理")) return;
            await SaveExercisesAsync(SrtQuestionPlanner.Plan(_vm.Cues, _vm.Project.Exercises.ClozeWordIndexes));
        })), WorkspaceUi.Text($"{_vm.Project.Exercises.Materials.Count} 段材料 · {_vm.Project.Exercises.Questions.Count} 道题")));
        foreach (var material in _vm.Project.Exercises.Materials)
        {
            var prompt = WorkspaceUi.Input(material.Prompt, "材料提示");
            var cueIndexes = WorkspaceUi.Input(Indexes(material.CueIndexes)); var repeated = WorkspaceUi.Input(Indexes(material.RepeatedCueIndexes)); var leadIn = WorkspaceUi.Input(Indexes(material.LeadInCueIndexes));
            var panel = WorkspaceUi.Stack(WorkspaceUi.Text($"材料 · 句 {Indexes(material.CueIndexes)}", 15, true),
                WorkspaceUi.Field("提示文本", prompt), WorkspaceUi.Row(WorkspaceUi.Field("材料句号", cueIndexes), WorkspaceUi.Field("重复朗读句号", repeated), WorkspaceUi.Field("题前提示句号", leadIn)),
                WorkspaceUi.Row(WorkspaceUi.Button("保存材料", () => RunAsync(() => SaveExercisesAsync(TeacherWorkspaceViewModel.UpdateMaterial(_vm.Project!.Exercises, material with { Prompt = prompt.Text ?? "", CueIndexes = ReadIndexes(cueIndexes.Text), RepeatedCueIndexes = ReadIndexes(repeated.Text), LeadInCueIndexes = ReadIndexes(leadIn.Text) }, _vm.Cues.Count)))),
                    WorkspaceUi.Button("添加小题", () => RunAsync(() => SaveExercisesAsync(TeacherWorkspaceViewModel.AddQuestion(_vm.Project!.Exercises, material.Id))))));
            foreach (var question in _vm.Project.Exercises.Questions.Where(q => q.MaterialId == material.Id))
            {
                var title = WorkspaceUi.Input(question.Title); var number = new NumericUpDown { Minimum = 1, Maximum = 999, Value = question.Number, Width = 90 };
                var options = WorkspaceUi.Input(string.Join('\n', question.Options), "每行一个选项", true); var answer = new NumericUpDown { Minimum = 0, Maximum = 99, Value = question.AnswerIndex + 1 ?? 0, Width = 90 };
                panel.Children.Add(new Separator()); panel.Children.Add(WorkspaceUi.Row(WorkspaceUi.Field("题号", number), WorkspaceUi.Field("题目", title)));
                panel.Children.Add(WorkspaceUi.Field("选项", options)); panel.Children.Add(WorkspaceUi.Field("答案序号（0 为未指定）", answer));
                panel.Children.Add(WorkspaceUi.Row(WorkspaceUi.Button("保存题目", () => RunAsync(() => SaveExercisesAsync(TeacherWorkspaceViewModel.UpdateQuestion(_vm.Project!.Exercises, question with
                { Title = title.Text?.Trim() ?? "", Number = (int)(number.Value ?? 1), Options = (options.Text ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(), AnswerIndex = answer.Value > 0 ? (int)answer.Value - 1 : null })))),
                    WorkspaceUi.Button("删除小题", () => RunAsync(async () => { if (await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "删除题目", question.Title, "删除")) await SaveExercisesAsync(TeacherWorkspaceViewModel.RemoveQuestion(_vm.Project!.Exercises, question.Id)); }))));
            }
            _exercises.Children.Add(WorkspaceUi.Surface(panel));
        }
    }
    private static string Indexes(IEnumerable<int> indexes) => string.Join(", ", indexes.Select(i => i + 1));
    private static int[] ReadIndexes(string? text) => string.IsNullOrWhiteSpace(text) ? [] : text.Split([',', '，', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s) - 1).Distinct().Order().ToArray();
    private static TimeSpan ParseTime(string? value) => TimeSpan.Parse((value ?? "").Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
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
        var choice = await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this), "新建制作工程", "创建空白工程，或选择音频开始制作。", "取消", "空白工程", "选择音频");
        if (choice == 0) return; var audio = choice == 2 ? await WorkspaceUi.Pick(this, "选择课程音频", "mp3", "wav", "m4a") : null;
        if (choice == 2 && audio == null) return; await SaveDraftAsync(); var p = await _store.CreateAsync(audio); if (p.AudioPath != null) { p = p with { AudioDuration = await VadSlicer.ProbeDurationAsync(p.AudioPath) }; await _store.SaveAsync(p); } await _vm.OpenAsync(p.Id); RenderProject();
    }
    private async Task BindAudioAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Pick(this, "绑定课程音频", "mp3", "wav", "m4a"); if (path == null) return; RetireTranscription(_vm.Project); await _vm.SaveAsync(await _store.BindAudioAsync(_vm.Project, path, await VadSlicer.ProbeDurationAsync(path))); RenderProject(); }
    private async Task ImportSrtAsync()
    {
        await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Pick(this, "导入字幕", "srt"); if (path == null) return;
        var text = await File.ReadAllTextAsync(path); var cues = SrtParser.Parse(Encoding.UTF8.GetBytes(text), _vm.Project.AudioDuration ?? TimeSpan.FromDays(7));
        var preserve = _vm.Cues.Count == cues.Count && _vm.Cues.Zip(cues).All(pair => pair.First.Start == pair.Second.Start && pair.First.End == pair.Second.End);
        RetireTranscription(_vm.Project); await _vm.SaveAsync(_vm.Project with { Transcript = text, Step = CourseProjectStep.Review, TranscriptionJobId = null,
            Exercises = preserve ? _vm.Project.Exercises : SrtQuestionPlanner.Plan(cues), AutomaticQuestionPlanApplied = true, AutoQuestionPlanDeferred = false }); RenderProject();
    }
    private async Task TranscribeAsync()
    {
        await SaveDraftAsync(); if (_vm.Project?.AudioPath == null) throw new InvalidOperationException("请先绑定音频。");
        if (!_settings().CloudReady) { _openSettings(); return; }
        var id = _queue.Enqueue(_vm.Project.Title, _vm.Project.AudioPath, _vm.Project.AudioDuration, _vm.Project.Id);
        await _vm.SaveAsync(_vm.Project with { TranscriptionJobId = id, Step = CourseProjectStep.Transcription, AutoQuestionPlanDeferred = false }); _status.Text = "已加入转写队列。";
    }
    private async Task ImportArchiveAsync() { var path = await WorkspaceUi.Pick(this, "导入制作工程", "zip"); if (path == null) return; await SaveDraftAsync(); var project = await _store.ImportZipAsync(path); await _vm.OpenAsync(project.Id); RenderProject(); }
    private async Task ExportArchiveAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Save(this, "导出制作工程", _vm.Project.Title + "-工程.zip", "zip"); if (path != null) await _store.ExportZipToFileAsync(_vm.Project, path); }
    private async Task ImportExamAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Pick(this, "导入 DOCX 试卷", "docx"); if (path == null) return; var document = await _store.ImportExamDocumentAsync(_vm.Project, path); await _vm.OpenAsync(document.Project.Id); RenderProject(); _status.Text = "试卷已导入。"; }
    private async Task ExportSrtAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Save(this, "导出课程字幕", _vm.Project.Title + ".srt", "srt"); if (path != null) await File.WriteAllTextAsync(path, _vm.Project.Transcript); }
    private async Task ExportExamAsync() { if (_vm.Project == null) return; var document = await _store.ReadExamDocumentAsync(_vm.Project); if (document == null) throw new InvalidOperationException("请先导入试卷。"); var path = await WorkspaceUi.Save(this, "导出试卷文本", _vm.Project.Title + "-试卷.txt", "txt"); if (path != null) await File.WriteAllTextAsync(path, document.Text); }
    private async Task FinishReviewAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; SrtParser.Parse(Encoding.UTF8.GetBytes(_vm.Project.Transcript), _vm.Project.AudioDuration ?? TimeSpan.FromDays(7)); await _vm.SaveAsync(_vm.Project with { Step = CourseProjectStep.Completed }); RenderProject(); }
    private async Task ExportIlpAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; var path = await WorkspaceUi.Save(this, "导出精听包", _vm.Project.Title + ".ilp", "ilp"); if (path == null) return; await new ProjectDelivery().CreateIlpAsync(_vm.Project, path); await _vm.SaveAsync(_vm.Project with { PackageVersion = _vm.Project.PackageVersion + 1, LastExportPath = path, Step = CourseProjectStep.Completed }); }
    private async Task AddToLibraryAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; var delivery = await new ProjectDelivery().AddToLibraryAsync(_vm.Project, _libraryDirectory); await _vm.SaveAsync(_vm.Project with { PackageVersion = delivery.PackageVersion, Step = CourseProjectStep.Completed }); _libraryChanged(); _status.Text = "课程已加入播放库。"; }
    public Func<CourseProject, Task<string?>>? StandaloneExporter { get; set; }
    private async Task ExportStandaloneAsync() { await SaveDraftAsync(); if (_vm.Project == null) return; if (StandaloneExporter == null) throw new InvalidOperationException("独立播放器导出服务尚未连接。"); var project=_vm.Project;var output=await StandaloneExporter(project);if(output is not null){await _vm.SaveAsync(project with{PackageVersion=project.PackageVersion+1,LastExportPath=output,Step=CourseProjectStep.Completed});RenderProject();} }
    private async Task DeleteAsync()
    {
        var project = _projectList.SelectedItem is ListBoxItem { Tag: string id } ? _vm.Projects.First(p => p.Id == id) : _vm.Project;
        if (project == null || !await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "删除制作工程", $"永久删除「{project.Title}」的音频、字幕和制作记录。", "删除")) return;
        RetireTranscription(project); await _store.DeleteAsync(project.Id); if (_vm.Project?.Id == project.Id) await _vm.OpenAsync(""); await RefreshAsync(); RenderProject();
    }
    private async Task RunAsync(Func<Task> action)
    {
        await _operations.WaitAsync();
        try { await action(); }
        catch (Exception ex) { _status.Text = ex.Message; IL.Core.Infrastructure.AppLog.Error("制作工程操作失败", ex); }
        finally { _operations.Release(); }
    }
}
