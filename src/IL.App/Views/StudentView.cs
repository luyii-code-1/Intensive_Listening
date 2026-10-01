using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using IL.App.ViewModels;
using IL.App.Views.Dialogs;
using IL.Core.Models;
using IL.Core.Student;

namespace IL.App.Views;

public sealed class StudentView : UserControl
{
    public Action<DictionaryQuery>? LookupWord { get; set; }
    private readonly StudentViewModel _vm;
    private readonly Grid _page = new() { RowDefinitions = new RowDefinitions("auto,*") };
    private readonly ContentControl _header = new();
    private readonly ContentControl _body = new() { [Grid.RowProperty] = 1 };
    private readonly StackPanel _transcriptContent = new() { Spacing = 10 };
    private readonly ScrollViewer _transcript = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly ContentControl _questions = new();
    private readonly StackPanel _index = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Dictionary<int, List<(Control Control, LessonTextPart Part)>> _words = [];
    private readonly Dictionary<int, Border> _cueRows = [];
    private readonly List<(Border Header, LessonMaterial Material)> _materialHeaders = [];
    private readonly List<(Expander Expander, IReadOnlyList<int> Cues)> _expanders = [];
    private readonly Dictionary<string, int> _selectedAnswers = [];
    private readonly HashSet<string> _revealedAnswers = [];
    private readonly Dictionary<string, Button> _indexButtons = [];
    private readonly Button _materialCloze, _allCloze, _return, _play;
    private readonly CheckBox _hideSubtitles = new() { Content = "隐藏字幕", VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _followTimer;
    private readonly DispatcherTimer _pausedBrowseTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private string? _displayedLessonId, _questionMaterialId;
    private bool _displayingMedia, _displayingTranscript, _importing, _handlingOperationStatus;
    private string? _openingLessonId, _deletingLessonId;
    private DateTimeOffset _followPausedUntil;

    public StudentView(StudentViewModel vm)
    {
        _vm = vm; DataContext = vm;
        _materialCloze = WorkspaceUi.Button("显示本段挖空", () => { vm.ToggleMaterialCloze(); return Task.CompletedTask; });
        _allCloze = WorkspaceUi.Button("显示全部挖空", () => { vm.ShowAllCloze = !vm.ShowAllCloze; return Task.CompletedTask; });
        _return = WorkspaceUi.Button("返回", async () => { _followPausedUntil = DateTimeOffset.MinValue; _pausedBrowseTimer.Stop(); await vm.ReturnCueCommand.ExecuteAsync(null); FollowActiveCue(); });
        _return.Bind(IsVisibleProperty, new Binding(nameof(vm.CanReturn)));
        _play = new Button { Command = vm.TogglePlaybackCommand, Padding = new Thickness(18, 12) };
        _play.Classes.Add("accent"); _play.Content = WorkspaceUi.Icon("play_solid", 22);
        _hideSubtitles.IsCheckedChanged += (_, _) => _vm.ShowSubtitles = _hideSubtitles.IsChecked != true;
        _play.Bind(IsEnabledProperty, new Binding(nameof(vm.CanPlay)));
        _transcript.Content = _transcriptContent;
        _transcript.PointerWheelChanged += (_, _) =>
        {
            _followPausedUntil = vm.IsPlaying ? DateTimeOffset.UtcNow.AddSeconds(5) : DateTimeOffset.MaxValue;
            _pausedBrowseTimer.Stop(); if (!vm.IsPlaying) _pausedBrowseTimer.Start();
        };
        _pausedBrowseTimer.Tick += async (_, _) =>
        {
            _pausedBrowseTimer.Stop(); if (vm.IsPlaying) return;
            var center = _transcript.Offset.Y + _transcript.Viewport.Height / 2;
            var nearest = _cueRows.Where(pair => pair.Value.IsEffectivelyVisible).Select(pair => (Index: pair.Key, Point: pair.Value.TranslatePoint(new Point(0, pair.Value.Bounds.Height / 2), _transcriptContent)))
                .Where(item => item.Point is { } point && point.Y >= _transcript.Offset.Y && point.Y <= _transcript.Offset.Y + _transcript.Viewport.Height)
                .OrderBy(item => Math.Abs(item.Point!.Value.Y - center)).FirstOrDefault();
            var material = nearest.Point is null ? null : vm.CurrentLesson?.Manifest.Exercises.MaterialForCue(nearest.Index);
            if (material is { QuestionIds.Count: > 0, CueIndexes.Count: > 0 } && material.CueIndexes[0] != vm.ActiveCue?.Index)
            { _followPausedUntil = DateTimeOffset.MinValue; await vm.JumpCueCommand.ExecuteAsync(vm.Cues[material.CueIndexes[0]]); }
        };
        _page.Children.Add(_header); _page.Children.Add(_body);
        Content = _page;
        _page.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!_vm.CanReturn) return;
            for (var control = e.Source as Control; control is not null; control = control.Parent as Control)
                if (ReferenceEquals(control, _return)) return;
            _vm.DismissReturnCue();
        }, RoutingStrategies.Tunnel);
        vm.PropertyChanged += VmChanged;
        vm.PresentationChanged += UpdatePresentation;
        vm.Lessons.CollectionChanged += (_, _) => { if (!vm.HasMedia) BuildHome(); };
        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _followTimer.Tick += (_, _) => FollowActiveCue();
        AttachedToVisualTree += (_, _) => _followTimer.Start();
        DetachedFromVisualTree += (_, _) => { _followTimer.Stop(); _pausedBrowseTimer.Stop(); };
        KeyDown += async (_, e) =>
        {
            if (e.Source is TextBox) return;
            if (e.Key == Key.Space) { e.Handled = true; await vm.TogglePlaybackCommand.ExecuteAsync(null); }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Left or Key.Right)
            { e.Handled = true; await (e.Key == Key.Left ? vm.PreviousCueCommand : vm.NextCueCommand).ExecuteAsync(null); }
        };
        ShowPage();
    }

    private static Button Command(string text, System.Windows.Input.ICommand command) => new() { Content = text, Command = command, Padding = new Thickness(8, 6) };
    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }
    private static TextBlock Text(string text, double size = 14, bool strong = false) => WorkspaceUi.Text(text, size, strong);
    private static Border Card(Control child, Thickness padding)
    {
        var card = WorkspaceUi.Surface(child, padding);
        card.BorderThickness = new Thickness(1);
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("WorkspaceCardBorderBrush"));
        return card;
    }
    private CheckBox Toggle(string text, string property)
    {
        var control = new CheckBox { Content = text, VerticalAlignment = VerticalAlignment.Center };
        control.Bind(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return control;
    }
    private static void DetachControl(Control control)
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        else if (control.Parent is Decorator decorator) decorator.Child = null;
        else if (control.Parent is ContentControl content) content.Content = null;
    }
    private void ShowPage()
    {
        BuildHeader();
        if (!_vm.HasMedia)
        {
            _displayingMedia = false; _displayedLessonId = null; _questionMaterialId = null;
            BuildHome(); return;
        }
        if (_displayingMedia && _displayingTranscript == _vm.HasTranscript && _displayedLessonId == _vm.CurrentLesson?.Id) return;
        _displayingMedia = true; _displayingTranscript = _vm.HasTranscript; _displayedLessonId = _vm.CurrentLesson?.Id;
        _selectedAnswers.Clear(); _revealedAnswers.Clear(); _questionMaterialId = null;
        foreach (var control in new Control[] { _questions, _index, _materialCloze, _allCloze, _return, _play, _transcript, _hideSubtitles }) DetachControl(control);
        var player = BuildPlayer();
        if (!_vm.HasTranscript) _body.Content = player;
        else
        {
            BuildTranscript();
            var transcriptPane = new Grid { Margin = new Thickness(12, 12, 12, 0) };
            transcriptPane.Children.Add(_transcript);
            var returnCard = Card(_return, new Thickness(6)); returnCard.HorizontalAlignment = HorizontalAlignment.Right; returnCard.VerticalAlignment = VerticalAlignment.Top; returnCard.Margin = new Thickness(8);
            returnCard.Bind(IsVisibleProperty, new Binding(nameof(_vm.CanReturn)));
            transcriptPane.Children.Add(returnCard);
            _body.Content = new StudentSplitPanel(player, transcriptPane);
        }
        UpdatePresentation(); UpdateActiveCue();
    }
    private void BuildHeader()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,auto"), Margin = new Thickness(24, 22, 24, 20), MinHeight = 36 };
        if (_vm.HasMedia)
        {
            var title = new TextBlock { FontSize = 18, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            title.Bind(TextBlock.TextProperty, new Binding(nameof(_vm.Title))); ToolTip.SetTip(title, _vm.Title);
            var leading = new Grid { ColumnDefinitions = new ColumnDefinitions("auto,auto,auto") };
            var back = WorkspaceUi.IconButton("back", "返回主页", _vm.ReturnHomeAsync); back.Margin = new Thickness(-12, 0, 8, 0);
            leading.Children.Add(back); Grid.SetColumn(title, 1); title.Margin = new Thickness(0, 0, 12, 0); leading.Children.Add(title);
            var info = WorkspaceUi.Button("文件信息", ShowFileInfoAsync); Grid.SetColumn(info, 2); leading.Children.Add(info);
            header.SizeChanged += (_, e) => title.MaxWidth = Math.Max(0, e.NewSize.Width - 136);
            header.Children.Add(leading);
        }
        else
        {
            header.Children.Add(Text("学生端", 22, true));
            var openAudio = WorkspaceUi.Button("打开音频", OpenAudioAsync); openAudio.Content = Row(WorkspaceUi.Icon("music_in_collection"), Text("打开音频")); openAudio.IsEnabled = !_importing;
            var openLesson = WorkspaceUi.Button("打开课程", PickLessonAsync); openLesson.Content = Row(WorkspaceUi.Icon("open_file"), Text("打开课程")); openLesson.IsEnabled = _vm.Lessons.Count > 0;
            var import = WorkspaceUi.Button("导入精听包", ImportAsync, true); import.Content = Row(_importing ? new FAProgressRing { IsActive = true, Width = 16, Height = 16 } : WorkspaceUi.Icon("download"), Text("导入精听包")); import.IsEnabled = !_importing;
            var commands = Row(openAudio, openLesson, import); Grid.SetColumn(commands, 1); header.Children.Add(commands);
        }
        _header.Content = header;
    }
    private void BuildHome()
    {
        BuildHeader();
        if (_vm.Lessons.Count == 0)
        {
            var actions = Row(WorkspaceUi.Button("打开音频", OpenAudioAsync, true), WorkspaceUi.Button("导入精听包", ImportAsync)); actions.HorizontalAlignment = HorizontalAlignment.Center;
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360 };
            var icon = WorkspaceUi.Icon("open_file", 40); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 18);
            empty.Children.Add(icon); empty.Children.Add(new TextBlock { Text = "打开音频或精听包", FontSize = 17, FontWeight = FontWeight.Medium, TextAlignment = TextAlignment.Center });
            empty.Children.Add(new TextBlock { Text = "直接播放常见音频，或导入带有题目与挖空练习的 .ilp 精听包。", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 18) }); empty.Children.Add(actions);
            _body.Content = empty; return;
        }
        var lessons = new StackPanel { Spacing = 8 };
        foreach (var lesson in _vm.Lessons.OrderByDescending(item => _vm.ProgressFor(item.Id)?.LastOpenedAt ?? DateTimeOffset.MinValue)) lessons.Children.Add(LessonRow(lesson));
        var content = new Grid { RowDefinitions = new RowDefinitions("auto,*"), MaxWidth = 1200, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24, 4, 24, 24) };
        var title = Text("最近课程", 17, true); title.Margin = new Thickness(0, 0, 0, 12); content.Children.Add(title);
        content.Children.Add(new ScrollViewer { Content = lessons, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, [Grid.RowProperty] = 1 });
        _body.Content = content;
    }
    private Control LessonRow(LessonListItem lesson)
    {
        var progress = _vm.ProgressFor(lesson.Id);
        var position = TimeSpan.FromMilliseconds(progress?.PositionMs ?? 0);
        var fraction = lesson.Lesson.Manifest.Duration.TotalMilliseconds > 0 ? Math.Clamp(position.TotalMilliseconds / lesson.Lesson.Manifest.Duration.TotalMilliseconds, 0, 1) : 0;
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = lesson.Title, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(Text($"{FormatTime(position)} / {FormatTime(lesson.Lesson.Manifest.Duration)} · 版本 {lesson.Lesson.Manifest.PackageVersion}", 12));
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("auto,*,auto"), Margin = new Thickness(18, 0) };
        Control icon = _openingLessonId == lesson.Id ? new FAProgressRing { IsActive = true, Width = 22, Height = 22 } : WorkspaceUi.Icon("music_note", 22); icon.Margin = new Thickness(0, 0, 18, 0); content.Children.Add(icon);
        Grid.SetColumn(info, 1); content.Children.Add(info);
        var track = new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction, Foreground = Brush.Parse("#E81123"), Width = 148, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        Grid.SetColumn(track, 2); content.Children.Add(track);
        var open = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        open.Classes.Add("student-lesson-open");
        open.IsEnabled = _openingLessonId is null && _deletingLessonId is null;
        open.Click += async (_, _) => await OpenLessonFromHomeAsync(lesson);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,auto,auto"), Height = 86 };
        row.Children.Add(open); row.Children.Add(new Border { Width = 1, Height = 40, Background = Brush.Parse("#22000000"), [Grid.ColumnProperty] = 1 });
        var remove = WorkspaceUi.IconButton("delete", "移除课程", () => RemoveAsync(lesson)); remove.IsEnabled = _openingLessonId is null && _deletingLessonId is null;
        if (_deletingLessonId == lesson.Id) remove.Content = new FAProgressRing { IsActive = true, Width = 14, Height = 14 }; remove.Margin = new Thickness(8, 0); Grid.SetColumn(remove, 2); row.Children.Add(remove);
        var card = Card(row, new Thickness(0)); card.Height = 88;
        card.PointerEntered += (_, _) => { card.Background = Brush.Parse("#0BE81123"); card.BoxShadow = new BoxShadows(new BoxShadow { Color = Color.Parse("#14000000"), Blur = 10, OffsetY = 3 }); };
        card.PointerExited += (_, _) => { card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("CardBackgroundFillColorDefaultBrush")); card.BoxShadow = default; };
        return card;
    }
    private Control BuildPlayer()
    {
        _questions.HorizontalAlignment = HorizontalAlignment.Stretch; _questions.VerticalAlignment = VerticalAlignment.Center;
        var questionCard = Card(_questions, new Thickness(16)); questionCard.VerticalAlignment = VerticalAlignment.Center;
        var questionSpace = new Grid(); questionSpace.Children.Add(questionCard);
        var controls = BuildControls();
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,auto"), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.Children.Add(questionSpace); Grid.SetRow(controls, 1); grid.Children.Add(controls);
        grid.SizeChanged += (_, e) =>
        {
            var controlsHeight = Math.Min(Math.Clamp(e.NewSize.Height / 2.68, 244, 320), e.NewSize.Height);
            controls.Height = controlsHeight;
            questionCard.MaxHeight = Math.Clamp(e.NewSize.Height - controlsHeight - 20, 0, 520);
        };
        var player = new Border { Padding = new Thickness(20, 8, 20, 16), Child = grid };
        UpdateQuestions(); BuildQuestionIndex(); return player;
    }
    private Control BuildControls()
    {
        var seek = new Slider { Minimum = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        seek.Bind(Slider.MaximumProperty, new Binding(nameof(_vm.DurationSeconds))); seek.Bind(Slider.ValueProperty, new Binding(nameof(_vm.SeekPositionSeconds)) { Mode = BindingMode.TwoWay });
        seek.AddHandler(PointerPressedEvent, (_, _) => _vm.IsScrubbing = true, RoutingStrategies.Tunnel);
        seek.AddHandler(PointerReleasedEvent, async (_, _) => { _vm.IsScrubbing = false; await _vm.SeekAsync(seek.Value); }, RoutingStrategies.Bubble, true);
        seek.KeyDown += (_, _) => _vm.IsScrubbing = true; seek.KeyUp += async (_, _) => { _vm.IsScrubbing = false; await _vm.SeekAsync(seek.Value); };
        var times = new Grid { ColumnDefinitions = new ColumnDefinitions("*,auto"), Margin = new Thickness(2, 0) };
        var position = Text("", 12); position.Bind(TextBlock.TextProperty, new Binding(nameof(_vm.PositionLabel))); times.Children.Add(position);
        var duration = Text("", 12); duration.Bind(TextBlock.TextProperty, new Binding(nameof(_vm.DurationLabel))); Grid.SetColumn(duration, 1); times.Children.Add(duration);
        var transport = Row(Command("上一题", _vm.PreviousQuestionCommand), Command("上一句", _vm.PreviousCueCommand), _play, Command("下一句", _vm.NextCueCommand), Command("下一题", _vm.NextQuestionCommand));
        transport.HorizontalAlignment = HorizontalAlignment.Center;
        transport.Children[0].IsEnabled = transport.Children[4].IsEnabled = _vm.CurrentLesson?.Manifest.Exercises.Questions.Count > 0;
        transport.Children[1].IsEnabled = transport.Children[3].IsEnabled = _vm.HasTranscript;
        var stack = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(seek); stack.Children.Add(times); stack.Children.Add(new Viewbox { Child = transport, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
        if (_vm.HasTranscript)
        {
            var toggles = Row(Toggle("单句循环", nameof(_vm.SingleSentenceLoop)), _hideSubtitles); toggles.Spacing = 18; toggles.HorizontalAlignment = HorizontalAlignment.Center; toggles.Margin = new Thickness(0, 12, 0, 0); stack.Children.Add(toggles);
            var cloze = new StudentCenteredWrapPanel(Command("重复本句", _vm.RepeatSentenceCommand), _materialCloze, _allCloze); cloze.Margin = new Thickness(0, 8, 0, 0); stack.Children.Add(cloze);
            if (_vm.CurrentLesson?.Manifest.Exercises.EffectiveMaterials.Any(m => m.QuestionIds.Count > 0) == true)
            {
                var strip = new Grid { ColumnDefinitions = new ColumnDefinitions("auto,*"), Height = 44, Margin = new Thickness(0, 8, 0, 0) };
                var label = Text("题目索引", 12); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 12, 0); strip.Children.Add(label);
                strip.Children.Add(new ScrollViewer { Content = _index, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, [Grid.ColumnProperty] = 1 }); stack.Children.Add(strip);
            }
        }
        return new Grid { Children = { stack } };
    }
    private async Task NavigateMaterialAsync(LessonMaterial material)
    {
        _followPausedUntil = DateTimeOffset.MinValue; _pausedBrowseTimer.Stop();
        await _vm.JumpMaterialAsync(material);
        FollowActiveCue();
    }
    private void BuildQuestionIndex()
    {
        _index.Children.Clear(); _indexButtons.Clear();
        if (_vm.CurrentLesson is not { } lesson) return;
        foreach (var material in lesson.Manifest.Exercises.EffectiveMaterials.Where(m => m.QuestionIds.Count > 0))
        {
            var numbers = lesson.Manifest.Exercises.QuestionsForMaterial(material).Select(q => q.Number).Where(n => n > 0).Order().ToArray();
            var label = numbers.Length == 0 ? "未编号" : $"{(numbers.Length > 1 && numbers[^1] - numbers[0] + 1 == numbers.Length ? $"{numbers[0]}–{numbers[^1]}" : string.Join('、', numbers))} 题";
            var button = WorkspaceUi.Button(label, () => NavigateMaterialAsync(material)); _index.Children.Add(button); _indexButtons[material.Id] = button;
        }
    }
    private void UpdateQuestions()
    {
        var id = _vm.ActiveMaterial?.Id;
        if (_questionMaterialId == id && _questions.Content is not null) return;
        _questionMaterialId = id;
        _selectedAnswers.Clear(); _revealedAnswers.Clear();
        if (_vm.Questions.Count == 0)
        { _questions.Content = new TextBlock { Text = "当前题目未设置", FontSize = 17, FontWeight = FontWeight.Medium, HorizontalAlignment = HorizontalAlignment.Center }; return; }
        var panel = new StackPanel { Spacing = 14 };
        foreach (var question in _vm.Questions) panel.Children.Add(QuestionTemplate(question));
        _questions.Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private Control QuestionTemplate(LessonQuestion question)
    {
        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(Text($"第 {question.Number} 题  {question.Title}", 20, true));
        var options = new ListBox { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ItemsSource = question.Options.Select((option, index) => $"{(char)('A' + index)}. {option}").ToArray() };
        if (_selectedAnswers.TryGetValue(question.Id, out var selected)) options.SelectedIndex = selected;
        options.SelectionChanged += (_, _) => { if (options.SelectedIndex >= 0) _selectedAnswers[question.Id] = options.SelectedIndex; };
        if (question.Options.Count > 0) panel.Children.Add(options);
        if (question.AnswerIndex is { } answer)
        {
            var answerText = Text($"答案：{(char)('A' + answer)}", 14, true); answerText.IsVisible = _revealedAnswers.Contains(question.Id);
            var reveal = new Button { Content = answerText.IsVisible ? "隐藏答案" : "显示答案", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
            reveal.Click += (_, _) => { if (!_revealedAnswers.Add(question.Id)) _revealedAnswers.Remove(question.Id); answerText.IsVisible = _revealedAnswers.Contains(question.Id); reveal.Content = answerText.IsVisible ? "隐藏答案" : "显示答案"; };
            panel.Children.Add(reveal); panel.Children.Add(answerText);
        }
        return panel;
    }
    private void BuildTranscript()
    {
        _transcriptContent.Children.Clear(); _words.Clear(); _cueRows.Clear(); _materialHeaders.Clear(); _expanders.Clear();
        if (_vm.CurrentLesson is not { } lesson) return;
        var exercises = lesson.Manifest.Exercises;
        var groups = new List<(LessonMaterial? Material, List<int> Cues)>();
        foreach (var row in _vm.Cues)
        {
            var material = exercises.MaterialForCue(row.Index);
            if (groups.Count == 0 || groups[^1].Material?.Id != material?.Id) groups.Add((material, []));
            groups[^1].Cues.Add(row.Index);
        }
        foreach (var (material, indexes) in groups)
        {
            if (material is null)
            {
                var header = new Grid { ColumnDefinitions = new ColumnDefinitions("auto,*,auto") };
                var icon = WorkspaceUi.Icon("info", 17); icon.Margin = new Thickness(0, 0, 10, 0); header.Children.Add(icon);
                var title = Text("题前提示", 14, true); Grid.SetColumn(title, 1); header.Children.Add(title);
                var count = Text($"{indexes.Count} 句", 12); Grid.SetColumn(count, 2); header.Children.Add(count);
                var expander = new Expander { Header = header, Content = CueRows(indexes), IsExpanded = indexes.Contains(_vm.ActiveCue?.Index ?? -1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                _expanders.Add((expander, indexes)); _transcriptContent.Children.Add(expander); continue;
            }
            var questions = exercises.QuestionsForMaterial(material);
            var numbers = string.Join('、', questions.Select(q => q.Number).Where(n => n > 0));
            var titleText = numbers.Length == 0 ? "听力材料" : $"第 {numbers} 题";
            var groupContent = new StackPanel { Spacing = 0 };
            var groupHeader = new Border { Padding = new Thickness(14, 11), Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { WorkspaceUi.Icon("checkbox_composite", 17), Text(titleText, 14, true) } } };
            _materialHeaders.Add((groupHeader, material)); groupContent.Children.Add(groupHeader); groupContent.Children.Add(Divider());
            var repeated = indexes.Where(material.RepeatedCueIndexes.Contains).ToArray();
            var primary = indexes.Where(i => !material.RepeatedCueIndexes.Contains(i)).ToArray();
            if (primary.Length > 0) groupContent.Children.Add(CueRows(primary));
            if (repeated.Length > 0)
            {
                groupContent.Children.Add(Divider());
                var repeatHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("auto,*,auto") };
                var repeatIcon = WorkspaceUi.Icon("sync", 16); repeatIcon.Margin = new Thickness(0, 0, 8, 0); repeatHeader.Children.Add(repeatIcon);
                var repeatText = Text("重复朗读"); Grid.SetColumn(repeatText, 1); repeatHeader.Children.Add(repeatText);
                var repeatCount = Text($"{repeated.Length} 句"); Grid.SetColumn(repeatCount, 2); repeatHeader.Children.Add(repeatCount);
                var expander = new Expander { Header = repeatHeader, Content = CueRows(repeated), IsExpanded = repeated.Contains(_vm.ActiveCue?.Index ?? -1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                _expanders.Add((expander, repeated)); groupContent.Children.Add(expander);
            }
            var card = Card(groupContent, new Thickness(0)); card.ClipToBounds = true; _transcriptContent.Children.Add(card);
        }
        _transcriptContent.Margin = new Thickness(0, 0, 0, 16);
    }
    private static Border Divider()
    {
        var divider = new Border { Height = 1 };
        divider.Bind(Border.BackgroundProperty, new DynamicResourceExtension("WorkspaceCardBorderBrush")); return divider;
    }
    private Control CueRows(IReadOnlyList<int> indexes)
    {
        var panel = new StackPanel();
        for (var i = 0; i < indexes.Count; i++)
        {
            if (i > 0) panel.Children.Add(Divider());
            panel.Children.Add(CueTemplate(_vm.Cues[indexes[i]]));
        }
        return panel;
    }
    private Control CueTemplate(CueRow row)
    {
        var text = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var words = new List<(Control, LessonTextPart)>();
        foreach (var part in LessonTextTokenizer.Tokenize(row.Text))
        {
            Control control;
            if (part.IsWord)
            {
                var cloze = _vm.CurrentLesson?.Manifest.Exercises.ClozeWordIndexes.GetValueOrDefault(row.Index)?.Contains(part.WordIndex!.Value) == true;
                var label = new TextBlock { Text = part.Text, FontSize = _vm.TranscriptFontSize, LineHeight = _vm.TranscriptFontSize * 1.4 };
                var word = new Border { Child = label, CornerRadius = new CornerRadius(4), Margin = new Thickness(cloze ? 2 : 1, cloze ? 1 : 0), Padding = cloze ? new Thickness(4, 1) : new Thickness(0) };
                if (cloze)
                {
                    word.Cursor = new Cursor(StandardCursorType.Hand); word.Focusable = true;
                    word.PointerReleased += async (_, e) => { if (e.InitialPressMouseButton != MouseButton.Left) return; e.Handled = true; await _vm.ToggleClozeAsync(row.Index, part.WordIndex!.Value); };
                    word.KeyDown += async (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; await _vm.ToggleClozeAsync(row.Index, part.WordIndex!.Value); } };
                }
                var lookup = new MenuItem { Header = "查词" }; lookup.Click += (_, _) => LookupWord?.Invoke(new(part.Text, row.Text, row.Index));
                word.ContextMenu = new ContextMenu { ItemsSource = new[] { lookup } }; control = word;
            }
            else control = new TextBlock { Text = part.Text, FontSize = _vm.TranscriptFontSize, LineHeight = _vm.TranscriptFontSize * 1.4, VerticalAlignment = VerticalAlignment.Center };
            words.Add((control, part)); text.Children.Add(control);
        }
        _words[row.Index] = words;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("auto,*"), Margin = new Thickness(14, 12), MinHeight = 50 };
        var time = Text(row.Time, 12); time.VerticalAlignment = VerticalAlignment.Center; time.Margin = new Thickness(0, 0, 16, 0); grid.Children.Add(time);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var visual = new Grid(); visual.Children.Add(grid);
        var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12), Background = Brush.Parse("#E81123"), IsVisible = false }; visual.Children.Add(marker);
        var border = new Border { Child = visual, MinHeight = 76, CornerRadius = new CornerRadius(4), Focusable = true, Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
        _cueRows[row.Index] = border;
        var playSelected = WorkspaceUi.Button("播放", () => _vm.PlaySelectedCueAsync(row, false));
        var playPause = WorkspaceUi.Button("播放并暂停", () => _vm.PlaySelectedCueAsync(row, true));
        var popup = new Flyout { Content = Row(playSelected, playPause), Placement = PlacementMode.Bottom };
        playSelected.Click += (_, _) => popup.Hide(); playPause.Click += (_, _) => popup.Hide();
        FlyoutBase.SetAttachedFlyout(border, popup);
        async Task SelectCue()
        {
            _followPausedUntil = _vm.IsPlaying ? DateTimeOffset.UtcNow.AddSeconds(5) : DateTimeOffset.MaxValue; _pausedBrowseTimer.Stop();
            await _vm.JumpCueCommand.ExecuteAsync(row);
            if (!_vm.IsPlaying) FlyoutBase.ShowAttachedFlyout(border);
        }
        border.PointerReleased += async (_, e) => { if (e.Handled || e.InitialPressMouseButton != MouseButton.Left) return; await SelectCue(); };
        border.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SelectCue(); } };
        var playItem = new MenuItem { Header = "播放" }; playItem.Click += async (_, _) => await _vm.PlaySelectedCueAsync(row, false);
        var pauseItem = new MenuItem { Header = "播放并暂停" }; pauseItem.Click += async (_, _) => await _vm.PlaySelectedCueAsync(row, true);
        border.ContextMenu = new ContextMenu { ItemsSource = new[] { playItem, pauseItem } };
        return border;
    }
    private void UpdatePresentation()
    {
        _hideSubtitles.IsChecked = !_vm.ShowSubtitles;
        _transcriptContent.Effect = !_vm.ShowSubtitles ? new BlurEffect { Radius = 8 } : null;
        foreach (var (cue, words) in _words)
            foreach (var (control, part) in words)
            {
                if (control is Border { Child: TextBlock label } word)
                {
                    label.FontSize = _vm.TranscriptFontSize; label.LineHeight = _vm.TranscriptFontSize * 1.4;
                    var hidden = _vm.IsClozeHidden(cue, part.WordIndex!.Value);
                    label.Effect = hidden ? new BlurEffect { Radius = 4.5 } : null;
                    var cloze = _vm.CurrentLesson?.Manifest.Exercises.ClozeWordIndexes.GetValueOrDefault(cue)?.Contains(part.WordIndex.Value) == true;
                    word.Background = cloze ? Brush.Parse(hidden ? "#1FE81123" : "#0FE81123") : Brushes.Transparent;
                    word.BorderThickness = new Thickness(0, 0, 0, hidden ? 2 : 0); word.BorderBrush = Brush.Parse("#B90D1C");
                    if (cloze) ToolTip.SetTip(word, hidden ? "显示单词" : "隐藏单词");
                }
                else if (control is TextBlock text) { text.FontSize = _vm.TranscriptFontSize; text.LineHeight = _vm.TranscriptFontSize * 1.4; }
            }
        _materialCloze.Content = _vm.MaterialClozeVisible ? "隐藏本段挖空" : "显示本段挖空";
        _allCloze.Content = _vm.ShowAllCloze ? "隐藏全部挖空" : "显示全部挖空";
        _materialCloze.IsVisible = _vm.ActiveMaterial is not null;
    }
    private void UpdateActiveCue()
    {
        var active = _vm.ActiveCue?.Index ?? -1;
        foreach (var (index, row) in _cueRows)
        {
            if (index == active) row.Bind(Border.BackgroundProperty, new DynamicResourceExtension("SubtleFillColorSecondaryBrush"));
            else row.Background = Brushes.Transparent;
            if (row.Child is Grid grid && grid.Children[1] is Border marker) marker.IsVisible = index == active;
        }
        foreach (var (header, material) in _materialHeaders)
        {
            var selected = material.CueIndexes.Contains(active) || material.LeadInCueIndexes.Contains(active);
            header.Background = selected ? Brush.Parse("#14E81123") : Brushes.Transparent;
            if (header.Child is StackPanel groupHeader && groupHeader.Children[0] is TextBlock icon) icon.Foreground = selected ? Brush.Parse("#E81123") : null;
        }
        foreach (var (expander, cues) in _expanders) expander.IsExpanded = cues.Contains(active);
        foreach (var (id, button) in _indexButtons)
        { if (id == _vm.ActiveMaterial?.Id) { button.Classes.Add("accent"); button.BringIntoView(); } else button.Classes.Remove("accent"); }
        UpdateQuestions(); UpdatePresentation();
        DispatcherTimer.RunOnce(FollowActiveCue, TimeSpan.FromMilliseconds(420));
    }
    private void FollowActiveCue()
    {
        if (!_vm.FollowTranscript || DateTimeOffset.UtcNow < _followPausedUntil || _vm.ActiveCue is not { } cue || !_cueRows.TryGetValue(cue.Index, out var row)) return;
        var position = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), _transcriptContent);
        if (position is { } point)
            _transcript.Offset = new Vector(0, Math.Clamp(point.Y - _transcript.Viewport.Height / 2, 0, Math.Max(0, _transcript.Extent.Height - _transcript.Viewport.Height)));
    }
    private void VmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(_vm.HasMedia) or nameof(_vm.HasTranscript)) ShowPage();
        if (e.PropertyName == nameof(_vm.ActiveCue)) UpdateActiveCue();
        if (e.PropertyName == nameof(_vm.TranscriptFontSize)) UpdatePresentation();
        if (e.PropertyName == nameof(_vm.Status) && !_handlingOperationStatus && !string.IsNullOrWhiteSpace(_vm.Status)
            && !_vm.Status.StartsWith("选择课程") && !_vm.Status.Contains(" 条字幕 · ") && _vm.Status != "音频已打开")
        {
            var message = _vm.Status;
            Dispatcher.UIThread.Post(async () =>
            {
                if (TopLevel.GetTopLevel(this) is Window owner)
                    await AppDialogs.ChooseAsync(owner, "无法完成操作", message, "完成");
            });
        }
        if (e.PropertyName == nameof(_vm.IsPlaying))
        {
            _play.Content = WorkspaceUi.Icon(_vm.IsPlaying ? "pause" : "play_solid", 22); ToolTip.SetTip(_play, _vm.PlayLabel);
            if (_vm.IsPlaying) { _followPausedUntil = DateTimeOffset.MinValue; _pausedBrowseTimer.Stop(); }
        }
    }
    private async Task ShowFileInfoAsync()
    {
        var manifest = _vm.CurrentLesson?.Manifest;
        var path = _vm.AudioPath;
        if (path is null) return;
        var details = new List<string> { $"名称：{_vm.Title}", $"类型：{(manifest is null ? "普通音频" : "ILP 精听包")}", $"时长：{_vm.DurationLabel}" };
        if (manifest is not null) details.AddRange([$"精听包版本：{manifest.PackageVersion}", $"格式版本：{manifest.FormatVersion}", $"包 UUID：{manifest.PackageUuid}", $"字幕：{_vm.Cues.Count} 句"]);
        if (File.Exists(path)) details.Add($"音频大小：{new FileInfo(path).Length / (1024.0 * 1024):F2} MB");
        details.Add($"文件位置：{path}");
        var dialog = AppDialogs.Create(WorkspaceUi.Owner(this), "文件信息", new SelectableTextBlock { Text = string.Join('\n', details), MaxWidth = 500, TextWrapping = TextWrapping.Wrap }, 548);
        dialog.CloseButtonText = "关闭";
        await dialog.ShowAsync(WorkspaceUi.Owner(this));
    }
    private async Task PickLessonAsync()
    {
        var owner = WorkspaceUi.Owner(this);
        var list = new ListBox { ItemsSource = _vm.Lessons, ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LessonListItem>((item, _) => WorkspaceUi.Stack(Text(item!.Title), Text($"{FormatTime(item.Lesson.Manifest.Duration)} · {item.Lesson.Cues.Count} 句", 12))), Height = 360, Width = 420 };
        var dialog = AppDialogs.Create(owner, "打开课程", list, 468); dialog.CloseButtonText = "关闭";
        list.SelectionChanged += async (_, _) => { if (list.SelectedItem is LessonListItem lesson) { dialog.Hide(); await _vm.OpenLessonAsync(lesson); } };
        await dialog.ShowAsync(owner);
    }
    private async Task OpenAudioAsync() { if (await WorkspaceUi.Pick(this, "打开音频", "mp3", "m4a", "wav", "flac", "ogg") is { } file) await _vm.OpenAudioAsync(file); }
    private async Task OpenLessonFromHomeAsync(LessonListItem lesson)
    {
        _openingLessonId = lesson.Id; BuildHome();
        try { await _vm.OpenLessonAsync(lesson); }
        finally { _openingLessonId = null; if (!_vm.HasMedia) BuildHome(); }
    }
    private async Task RemoveAsync(LessonListItem lesson)
    {
        if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "移除课程？", $"将永久删除「{lesson.Title}」及其本地播放记录。", "永久删除")) return;
        _deletingLessonId = lesson.Id; _handlingOperationStatus = true; BuildHome();
        try
        {
            await _vm.RemoveByIdAsync(lesson.Id);
            if (_vm.Lessons.Any(item => item.Id == lesson.Id)) ShowNotice("移除失败", _vm.Status, true);
            else ShowNotice("课程已移除", lesson.Title);
        }
        finally { _deletingLessonId = null; _handlingOperationStatus = false; if (!_vm.HasMedia) BuildHome(); }
    }
    private void ShowNotice(string title, string message, bool error = false) =>
        WorkspaceToast.Show(this, title, message, error);
    private async Task<bool> ImportLessonAsync(string file, bool replace = false, string? title = null, string noticeTitle = "导入完成", string noticeMessage = "课程已保存到主页。")
    {
        await _vm.ImportAsync(file, replace, title);
        if (_vm.Status.StartsWith("导入失败"))
        { await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this), "无法导入", _vm.Status, "完成"); return false; }
        ShowNotice(noticeTitle, noticeMessage); return true;
    }
    private async Task ImportAsync() { if (await WorkspaceUi.Pick(this, "选择精听包", "ilp") is { } file) await ImportFileAsync(file); }
    private static string FormatTime(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
    private async Task<string?> RenameLessonAsync(string initialTitle)
    {
        var input = new TextBox { Text = initialTitle };
        var dialog = AppDialogs.Create(WorkspaceUi.Owner(this), "重命名课程", WorkspaceUi.Field("课程名称", input));
        dialog.CloseButtonText = "取消"; dialog.PrimaryButtonText = "导入"; dialog.DefaultButton = FAContentDialogButton.Primary;
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = string.IsNullOrWhiteSpace(input.Text);
        dialog.Opened += (_, _) => input.Focus();
        return await dialog.ShowAsync(WorkspaceUi.Owner(this)) == FAContentDialogResult.Primary ? input.Text!.Trim() : null;
    }
    public async Task ImportFileAsync(string file, bool standalone = false)
    {
        _importing = _handlingOperationStatus = true; BuildHeader();
        try
        {
            var manifest = await _vm.ReadManifestAsync(file); var existing = _vm.Lessons.FirstOrDefault(l => l.Id == manifest.PackageUuid);
            if (existing is not null)
            {
                if (existing.Lesson.Manifest.PackageVersion == manifest.PackageVersion)
                {
                    var local = existing.Lesson.Manifest;
                    if (local.AudioSha256 != manifest.AudioSha256 || local.TranscriptSha256 != manifest.TranscriptSha256 || local.Title != manifest.Title)
                        await ImportLessonAsync(file, true, noticeTitle: "课程已重载", noticeMessage: manifest.Title);
                    else { await _vm.OpenLessonAsync(existing); ShowNotice("课程已重载", manifest.Title); }
                    return;
                }
                if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "发现课程的其他版本", $"本地版本 {existing.Lesson.Manifest.PackageVersion}，文件版本 {manifest.PackageVersion}。是否更新？", "更新课程")) return;
                await ImportLessonAsync(file, true, noticeTitle: "课程已更新", noticeMessage: "播放记录与挖空记录已保留。"); return;
            }
            var sameTitle = _vm.Lessons.FirstOrDefault(l => l.Title.Equals(manifest.Title, StringComparison.OrdinalIgnoreCase));
            if (sameTitle is not null)
            {
                var choice = await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this), "课程名称已存在", $"主页已经有名为「{manifest.Title}」的课程。", "取消", "重命名导入", "覆盖本地课程");
                if (choice == 0) return;
                if (choice == 2) { if (!await ImportLessonAsync(file)) return; if (_vm.CurrentLesson?.Id == manifest.PackageUuid) await _vm.RemoveByIdAsync(sameTitle.Id); return; }
                if (choice == 1)
                {
                    var title = manifest.Title; var suffix = 2;
                    while (_vm.Lessons.Any(l => l.Title.Equals(title, StringComparison.OrdinalIgnoreCase))) title = $"{manifest.Title} ({suffix++})";
                    if (await RenameLessonAsync(title) is { } renamed) await ImportLessonAsync(file, false, renamed);
                    return;
                }
            }
            await ImportLessonAsync(file);
        }
        catch (Exception ex) { _vm.Status = ex.Message; await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this), "无法导入", ex.Message, "完成"); }
        finally { _importing = _handlingOperationStatus = false; BuildHeader(); }
    }
}
