using Avalonia.Collections;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using FluentAvalonia.UI.Windowing;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using IL.App.Services;
using IL.Core.Infrastructure;

namespace IL.App.Views.Dialogs;

public sealed class LiveLogWindow : FAAppWindow
{
    private readonly TextBox _search = new() { PlaceholderText = "搜索消息、组件或线程", MinWidth = 230 };
    private readonly ComboBox _level = new() { ItemsSource = new[] { "全部级别", "INFO", "DEBUG", "WARN", "ERROR", "TRACE" }, SelectedIndex = 0, MinWidth = 110 };
    private readonly ComboBox _source = new() { ItemsSource = new[] { "全部来源", "操作与应用", "first-chance", "network", "runtime", "trace" }, SelectedIndex = 0, MinWidth = 135 };
    private readonly ListBox _rows = new() { SelectionMode = SelectionMode.Multiple };
    private readonly AvaloniaList<DiagnosticEntry> _entries = new() { ResetBehavior = ResetBehavior.Reset };
    private readonly CheckBox _follow = new() { Content = "跟随最新", IsChecked = true };
    private readonly CheckBox _wrap = new() { Content = "详情自动换行", IsChecked = false };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap, FontFamily = new Avalonia.Media.FontFamily("Consolas"), FontSize = 12 };
    private readonly TextBlock _status = WorkspaceUi.Text("", 12);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly EventPipeRecorder _recorder = new();
    private bool _refreshing, _updatingRows;
    private int _filterRevision, _appliedRevision = -1, _detailRevision;
    private long _filterDue, _skippedView;
    private DiagnosticEntry? _detailEntry;
    private bool _paused, _allowClose;
    private Task? _stopTask;
    private long _clearedThrough, _lastSequence = -1;
    private readonly Button _sample;
    public LiveLogWindow()
    {
        Title = "Intensive Listening · 实时日志分析"; Icon = AppBrand.Image;
        ShowInTaskbar = true; ShowAsDialog = false;
        Width = 1180; Height = 760; MinWidth = 850; MinHeight = 520;
        var pause = WorkspaceUi.Button("暂停", () => { _paused = !_paused; return Task.CompletedTask; });
        pause.Click += (_, _) => { pause.Content = _paused ? "继续" : "暂停"; if (!_paused) RequestRefresh(); };
        var clear = WorkspaceUi.Button("清空视图", () => { _clearedThrough = LiveDiagnostics.LatestSequence; _skippedView = 0; RequestRefresh(); return Task.CompletedTask; });
        var export = WorkspaceUi.Button("导出缓冲", ExportAsync);
        _sample = WorkspaceUi.Button("开始 EventPipe 采样", ToggleSamplingAsync);
        var copy = WorkspaceUi.Button("复制选中", async () =>
        {
            var selected = _rows.SelectedItems!.OfType<DiagnosticEntry>().ToArray();
            if (selected.Length == 0 && _detailEntry != null) selected = [_detailEntry];
            var text = await Task.Run(() => string.Join("\n\n", selected.AsEnumerable()));
            if (Clipboard != null) await Clipboard.SetTextAsync(text);
        });
        var controls = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in new Control[] { _search, _level, _source, pause, clear, copy, export, _sample, _follow, _wrap }) { item.Margin = new Thickness(0, 0, 8, 8); controls.Children.Add(item); }
        var heading = WorkspaceUi.Stack(WorkspaceUi.Text("实时日志分析", 22, true), WorkspaceUi.Text("操作日志、首次抛出异常与 .NET 运行时事件。选中一行查看完整内容；EventPipe 采样保存为本机 .nettrace 文件。", 12));
        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,3*,6,2*,Auto") };
        heading.Margin = new Thickness(0, 0, 0, 16); grid.Children.Add(heading);
        Grid.SetRow(controls, 1); grid.Children.Add(controls);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("110,65,150,*"), Margin = new Thickness(12, 0, 12, 6) };
        foreach (var (label, index) in new[] { ("时间", 0), ("级别", 1), ("来源 / 线程", 2), ("内容", 3) })
        { var text = WorkspaceUi.Text(label, 12, true); Grid.SetColumn(text, index); header.Children.Add(text); }
        var sources = new WrapPanel { Orientation = Orientation.Horizontal };
        var sourceChoices = new[] { ("首次抛出异常", DiagnosticSources.FirstChance), ("网络请求明细", DiagnosticSources.NetworkDetails), ("GC / 线程池 / 计数器", DiagnosticSources.Runtime), ("全局 Trace", DiagnosticSources.Trace) };
        foreach (var (label, flag) in sourceChoices)
        {
            var check = new CheckBox { Content = label, IsChecked = RuntimeDiagnostics.Sources.HasFlag(flag), Margin = new Thickness(0, 0, 16, 0) };
            check.IsCheckedChanged += (_, _) => RuntimeDiagnostics.Sources = check.IsChecked == true ? RuntimeDiagnostics.Sources | flag : RuntimeDiagnostics.Sources & ~flag;
            sources.Children.Add(check);
        }
        var sourcePanel = WorkspaceUi.Stack(WorkspaceUi.Text("默认采集应用操作、警告、错误和网络失败。详细源仅在此窗口开启期间按需采集；高频事件会限流并计数。", 12), sources);
        var sourceExpander = new Expander { Header = "诊断数据源", Content = sourcePanel, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(sourceExpander, 2); grid.Children.Add(sourceExpander);
        Grid.SetRow(header, 3); grid.Children.Add(header);
        _rows.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _rows.ItemsSource = _entries;
        _rows.ItemTemplate = new FuncDataTemplate<DiagnosticEntry>((entry, _) =>
        {
            if (entry == null) return null;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("110,65,150,*"), Margin = new Thickness(0, 3) };
            var values = new[] { entry.Time.ToString("HH:mm:ss.fff"), entry.Level, entry.Category + " / T" + entry.ThreadId, entry.Preview };
            for (var index = 0; index < values.Length; index++)
            {
                var text = new TextBlock { Text = values[index], FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
                if (entry.Level is "ERROR" or "WARN") text.Foreground = entry.Level == "ERROR" ? Brushes.IndianRed : Brushes.DarkOrange;
                Grid.SetColumn(text, index); row.Children.Add(text);
            }
            return row;
        });
        Grid.SetRow(_rows, 4); grid.Children.Add(_rows);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(splitter, 5); grid.Children.Add(splitter);
        _details.Margin = new Thickness(0, 8, 0, 8); Grid.SetRow(_details, 6); grid.Children.Add(_details);
        Grid.SetRow(_status, 7); grid.Children.Add(_status); Content = grid;
        _rows.SelectionChanged += async (_, _) => { if (!_updatingRows) await ShowDetailsAsync(); };
        _wrap.IsCheckedChanged += (_, _) => _details.TextWrapping = _wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _search.TextChanged += (_, _) => RequestRefresh(); _level.SelectionChanged += (_, _) => RequestRefresh(); _source.SelectionChanged += (_, _) => RequestRefresh();
        _timer.Tick += async (_, _) => { if (!AppLog.DebugEnabled) { Close(); return; } await RefreshAsync(); };
        Opened += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        Closing += async (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            await StopCaptureAsync();
        };
    }
    public Task StopCaptureAsync() => _stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        _timer.Stop(); RuntimeDiagnostics.Sources = DiagnosticSources.Default;
        try { await _recorder.DisposeAsync(); }
        catch (Exception error) { AppLog.Warning("停止 EventPipe 采样失败", error); }
        finally { _allowClose = true; Close(); }
    }
    private void RequestRefresh() { _filterRevision++; _filterDue = Environment.TickCount64 + 250; }
    private async Task RefreshAsync()
    {
        if (_refreshing || _paused || _stopTask != null || !IsVisible || Environment.TickCount64 < _filterDue) return;
        var revision = _filterRevision; var reset = revision != _appliedRevision;
        if (!reset && LiveDiagnostics.LatestSequence == _lastSequence) return;
        var level = _level.SelectedIndex <= 0 ? null : _level.SelectedItem as string;
        var source = _source.SelectedIndex <= 0 ? null : _source.SelectedItem as string;
        var query = _search.Text ?? ""; var cleared = _clearedThrough; var after = _lastSequence;
        _refreshing = true;
        try
        {
            var frame = await Task.Run(() => DiagnosticLogView.Read(LiveDiagnostics.Snapshot(), query, level, source, cleared, after, reset));
            if (_stopTask != null || _paused || revision != _filterRevision) return;
            _updatingRows = true;
            try
            {
                if (reset) _entries.Clear();
                var remove = _entries.TakeWhile(entry => entry.Sequence < frame.Oldest).Count();
                remove = Math.Max(remove, Math.Max(0, _entries.Count + frame.Entries.Length - DiagnosticLogView.Capacity));
                if (remove > 0) _entries.RemoveRange(0, remove);
                _entries.AddRange(frame.Entries);
            }
            finally { _updatingRows = false; }
            _lastSequence = frame.Latest; _appliedRevision = revision; _skippedView += frame.Skipped;
            if (_follow.IsChecked == true && _rows.SelectedItem == null && frame.Entries.Length > 0) _rows.ScrollIntoView(_entries[^1]);
            _status.Text = $"显示 {_entries.Count}/{DiagnosticLogView.Capacity} 条 · 缓冲 {frame.Buffered} 条 / 筛选匹配 {frame.Matching} 条 · 界面快速跳过 {_skippedView} 条\n详细源限流 {RuntimeDiagnostics.ThrottledEntries} 条 · 采集或写入过载 {RuntimeDiagnostics.DroppedEntries} 条 · 会话文件：{RuntimeDiagnostics.SessionLogPath}";
            if (_recorder.Path != null) _status.Text += "\n" + (_recorder.IsRecording ? "正在采样：" : "采样文件：") + _recorder.Path;
        }
        catch (Exception error) { _status.Text = "日志视图刷新失败：" + error.Message; }
        finally { _refreshing = false; }
    }
    private async Task ShowDetailsAsync()
    {
        var revision = ++_detailRevision;
        var entry = _rows.SelectedItem as DiagnosticEntry;
        _detailEntry = entry;
        if (entry == null) { _details.Text = ""; return; }
        var text = await Task.Run(() =>
        {
            var text = entry.ToString();
            return text.Length <= 32768 ? text : text[..32768] + "\n\n详情较长，当前预览前 32768 字符。点击「复制选中」可复制完整内容。";
        });
        if (revision == _detailRevision && _stopTask == null) _details.Text = text;
    }
    private async Task ExportAsync()
    {
        var path = await WorkspaceUi.Save(this, "导出实时日志", $"IL-实时日志-{DateTime.Now:yyyyMMdd-HHmmss}.log", "log");
        if (path != null) await Task.Run(async () =>
        {
            var entries = LiveDiagnostics.Snapshot();
            await using var output = new StreamWriter(path);
            foreach (var entry in entries) await output.WriteLineAsync(entry.ToString());
        });
    }
    private async Task ToggleSamplingAsync()
    {
        _sample.IsEnabled = false;
        try
        {
            if (_recorder.IsRecording) { await _recorder.StopAsync(); _status.Text = "采样已保存：" + _recorder.Path; }
            else
            {
                var path = System.IO.Path.Combine(AppLog.DirectoryPath, $"IL-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.nettrace");
                await _recorder.StartAsync(path);
            }
        }
        catch (Exception error) { AppLog.Error("EventPipe 采样失败", error); _status.Text = error.Message; }
        finally { _sample.Content = _recorder.IsRecording ? "停止并保存采样" : "开始 EventPipe 采样"; _sample.IsEnabled = true; }
    }
}
