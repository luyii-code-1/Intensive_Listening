using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using IL.App.ViewModels;
using IL.App.Views.Dialogs;
using IL.Core.Models;
using IL.Core.Student;

namespace IL.App.Views;

public sealed class StudentView : UserControl
{
    public Action<DictionaryQuery>? LookupWord { get; set; }
    private readonly StudentViewModel _vm;
    private readonly ListBox _transcript;
    private readonly Dictionary<int, List<(Control Control, LessonTextPart Part)>> _words = [];
    public StudentView(StudentViewModel vm)
    {
        _vm = vm; DataContext = vm;
        var library = new ListBox { ItemsSource = vm.Lessons, ItemTemplate = new FuncDataTemplate<LessonListItem>((item, _) => WorkspaceUi.Stack(WorkspaceUi.Text(item!.Title, 14, true), WorkspaceUi.Text(item.Detail, 12))) };
        library.Bind(ListBox.SelectedItemProperty, new Binding(nameof(vm.SelectedLesson)) { Mode = BindingMode.TwoWay });
        var left = new DockPanel { LastChildFill = true, Margin = new Thickness(0,0,16,0) };
        var tools = WorkspaceUi.Stack(WorkspaceUi.Text("课程",18,true), WorkspaceUi.Row(WorkspaceUi.Button("导入课包", ImportAsync, true), WorkspaceUi.Button("打开音频", OpenAudioAsync)), WorkspaceUi.Button("移除课程", RemoveAsync));
        DockPanel.SetDock(tools, Dock.Top); left.Children.Add(tools); left.Children.Add(library);
        _transcript = new ListBox { ItemsSource = vm.Cues, ItemTemplate = new FuncDataTemplate<CueRow>((row, _) => CueTemplate(row!)) };
        _transcript.Bind(ListBox.SelectedItemProperty, new Binding(nameof(vm.ActiveCue)) { Mode = BindingMode.OneWay });
        var title = new TextBlock { FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0,0,0,10) };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(vm.Title)));
        var options = WorkspaceUi.Row(Toggle("显示字幕", nameof(vm.ShowSubtitles)), Toggle("显示填空", nameof(vm.ShowAllCloze)), Toggle("跟随播放", nameof(vm.FollowTranscript)));
        var center = new DockPanel { LastChildFill = true };
        var header = WorkspaceUi.Stack(title, options); DockPanel.SetDock(header,Dock.Top); center.Children.Add(header); center.Children.Add(_transcript);
        var questions = new ItemsControl { ItemsSource = vm.Questions, ItemTemplate = new FuncDataTemplate<LessonQuestion>((q, _) => QuestionTemplate(q!)) };
        var questionPanel = WorkspaceUi.Stack(WorkspaceUi.Text("练习",18,true), WorkspaceUi.Row(Command("上一题",vm.PreviousQuestionCommand), Command("下一题",vm.NextQuestionCommand)), Toggle("显示答案",nameof(vm.RevealAnswer)), WorkspaceUi.Button("切换本段填空",() => { vm.ToggleMaterialCloze();return Task.CompletedTask; }), questions);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,280"), Margin = new Thickness(20,16) };
        body.Children.Add(left);Grid.SetColumn(center,1);body.Children.Add(center);Grid.SetColumn(questionPanel,2); questionPanel.Margin = new Thickness(16,0,0,0);body.Children.Add(new ScrollViewer { Content = questionPanel, [Grid.ColumnProperty] = 2 });
        var seek = new Slider { Minimum = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        seek.Bind(Slider.MaximumProperty,new Binding(nameof(vm.DurationSeconds)));seek.Bind(Slider.ValueProperty,new Binding(nameof(vm.SeekPositionSeconds)){Mode=BindingMode.TwoWay});
        seek.AddHandler(PointerPressedEvent,(_,_)=>vm.IsScrubbing=true,RoutingStrategies.Tunnel);
        seek.AddHandler(PointerReleasedEvent,async(_,_)=>{vm.IsScrubbing=false;await vm.SeekAsync(seek.Value);},RoutingStrategies.Bubble, true);
        seek.KeyDown += (_,_)=>vm.IsScrubbing=true;seek.KeyUp += async(_,_)=>{vm.IsScrubbing=false;await vm.SeekAsync(seek.Value);};
        var rate = new ComboBox { ItemsSource=vm.PlaybackRates, MinWidth=84 };rate.Bind(ComboBox.SelectedItemProperty,new Binding(nameof(vm.PlaybackRate)){Mode=BindingMode.TwoWay});
        var volume = new Slider { Minimum=0,Maximum=1,Width=100 };volume.Bind(Slider.ValueProperty,new Binding(nameof(vm.Volume)){Mode=BindingMode.TwoWay});
        var play = Command("播放",vm.TogglePlaybackCommand);play.Classes.Add("accent");play.Bind(ContentControl.ContentProperty,new Binding(nameof(vm.PlayLabel)));play.Bind(IsEnabledProperty,new Binding(nameof(vm.CanPlay)));
        var position = new TextBlock { VerticalAlignment=VerticalAlignment.Center };position.Bind(TextBlock.TextProperty,new Binding(nameof(vm.PositionLabel)));
        var duration = new TextBlock { VerticalAlignment=VerticalAlignment.Center };duration.Bind(TextBlock.TextProperty,new Binding(nameof(vm.DurationLabel)));
        var status = new TextBlock { FontSize=12 };status.Bind(TextBlock.TextProperty,new Binding(nameof(vm.Status)));
        var footer = WorkspaceUi.Stack(seek, WorkspaceUi.Row(position,WorkspaceUi.Text("/"),duration,Command("上一句",vm.PreviousCueCommand),play,Command("下一句",vm.NextCueCommand),Command("单句重听",vm.RepeatSentenceCommand),Toggle("单句循环",nameof(vm.SingleSentenceLoop)),Command("返回",vm.ReturnCueCommand),WorkspaceUi.Text("倍速"),rate,WorkspaceUi.Text("音量"),volume),status);
        var footerBorder = new Border { Child=footer, Padding=new Thickness(24,12), BorderThickness=new Thickness(0,1,0,0), BorderBrush=Brushes.Gray };
        var layout = new DockPanel();DockPanel.SetDock(footerBorder,Dock.Bottom);layout.Children.Add(footerBorder);layout.Children.Add(body);Content=layout;
        vm.PresentationChanged += UpdateWords;vm.PropertyChanged += VmChanged;
        KeyDown += async(_,e)=>{if(e.Source is TextBox)return;if(e.Key==Key.Space){e.Handled=true;await vm.TogglePlaybackCommand.ExecuteAsync(null);}else if(e.KeyModifiers.HasFlag(KeyModifiers.Alt)&&e.Key is Key.Left or Key.Right){e.Handled=true;await(e.Key==Key.Left?vm.PreviousCueCommand:vm.NextCueCommand).ExecuteAsync(null);}};
    }
    private Button Command(string text,System.Windows.Input.ICommand command)=>new(){Content=text,Command=command};
    private CheckBox Toggle(string text,string property){var control=new CheckBox{Content=text};control.Bind(CheckBox.IsCheckedProperty,new Binding(property){Mode=BindingMode.TwoWay});return control;}
    private Control CueTemplate(CueRow row)
    {
        var panel=new WrapPanel();var words=new List<(Control,LessonTextPart)>();
        foreach(var part in LessonTextTokenizer.Tokenize(row.Text))
        {
            Control control;
            if(part.IsWord)
            {
                var button=new Button { Content=new TextBlock{Text=part.Text,FontSize=_vm.TranscriptFontSize},Padding=new Thickness(0),MinHeight=0,MinWidth=0,Background=Brushes.Transparent,BorderThickness=new Thickness(0) };
                button.Click += async(_,_)=>await _vm.ToggleClozeAsync(row.Index,part.WordIndex!.Value);
                var lookup=new MenuItem{Header="查词"};lookup.Click+=async(_,_)=>await Task.Run(() => LookupWord?.Invoke(new(part.Text,row.Text,row.Index)));
                button.ContextMenu=new ContextMenu{ItemsSource=new[]{lookup}};control=button;
            }
            else control=new TextBlock{Text=part.Text,FontSize=_vm.TranscriptFontSize};
            words.Add((control,part));panel.Children.Add(control);
        }
        _words[row.Index]=words;ApplyWords(row.Index,words);
        var time=new Button{Content=row.Time,Command=_vm.JumpCueCommand,CommandParameter=row,VerticalAlignment=VerticalAlignment.Top};
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("80,*"),Margin=new Thickness(0,6)};grid.Children.Add(time);Grid.SetColumn(panel,1);grid.Children.Add(panel);return grid;
    }
    private void ApplyWords(int cue,List<(Control Control,LessonTextPart Part)> words)
    {
        foreach(var (control,part) in words)
        {
            var hidden=!_vm.ShowSubtitles||part.IsWord&&_vm.IsClozeHidden(cue,part.WordIndex!.Value);
            control.Effect=hidden?new BlurEffect{Radius=_vm.ShowSubtitles?4.5:8}:null;
            if(control is TextBlock text)text.FontSize=_vm.TranscriptFontSize;
            else if(control is Button{Content:TextBlock label})label.FontSize=_vm.TranscriptFontSize;
        }
    }
    private void UpdateWords(){foreach(var pair in _words)ApplyWords(pair.Key,pair.Value);}
    private void VmChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(_vm.ActiveCue)&&_vm.FollowTranscript&&_vm.ActiveCue is {} row)_transcript.ScrollIntoView(row);
        if(e.PropertyName==nameof(_vm.TranscriptFontSize))UpdateWords();
    }
    private Control QuestionTemplate(LessonQuestion question)
    {
        var answer=new TextBlock{Text=$"答案：{(question.AnswerIndex is {} ai && ai < question.Options.Count ? question.Options[ai] : "未设置")}",TextWrapping=TextWrapping.Wrap};answer.Bind(IsVisibleProperty,new Binding(nameof(_vm.RevealAnswer)){Source=_vm});
        var panel=WorkspaceUi.Stack(WorkspaceUi.Text($"{question.Number}. {question.Title}",15,true));
        foreach(var option in question.Options)panel.Children.Add(WorkspaceUi.Text(option));panel.Children.Add(answer);panel.Margin=new Thickness(0,8,0,16);return panel;
    }
    private async Task OpenAudioAsync(){if(await WorkspaceUi.Pick(this,"打开音频","mp3","m4a","wav","flac","ogg") is {} file)await _vm.OpenAudioAsync(file);}
    private async Task RemoveAsync(){if(_vm.SelectedLesson is {} lesson&&await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this),"移除课程",$"移除“{lesson.Title}”及其学习进度？","移除"))await _vm.RemoveSelectedAsync();}
    private async Task ImportAsync(){if(await WorkspaceUi.Pick(this,"导入精听包","ilp") is {} file)await ImportFileAsync(file);}
    public async Task ImportFileAsync(string file,bool standalone=false)
    {
        try
        {
            var manifest=await _vm.ReadManifestAsync(file);var existing=_vm.Lessons.FirstOrDefault(l=>l.Id==manifest.PackageUuid);
            if(existing is not null)
            {
                if(standalone){_vm.SelectedLesson=existing;await _vm.CurrentLoad;return;}
                var label=existing.Lesson.Manifest.PackageVersion==manifest.PackageVersion?"覆盖":"更新";
                if(!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this),$"{label}课程",$"课库中已有“{existing.Title}” v{existing.Lesson.Manifest.PackageVersion}，导入 v{manifest.PackageVersion}？",label))return;
                await _vm.ImportAsync(file,true);return;
            }
            var sameTitle=_vm.Lessons.FirstOrDefault(l=>l.Title==manifest.Title);
            if(sameTitle is not null&&!standalone)
            {
                var choice=await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this),"课程重名",$"课库中已有“{manifest.Title}”。","取消","自动重命名","替换同名课程");
                if(choice==0)return;
                if(choice==2){await _vm.ImportAsync(file);if(_vm.CurrentLesson?.Id==manifest.PackageUuid)await _vm.RemoveByIdAsync(sameTitle.Id);return;}
                if(choice==1){var title=manifest.Title;var suffix=2;while(_vm.Lessons.Any(l=>l.Title==title))title=$"{manifest.Title} ({suffix++})";await _vm.ImportAsync(file,false,title);return;}
            }
            await _vm.ImportAsync(file);
        }
        catch(Exception ex){_vm.Status=ex.Message;}
    }
}
