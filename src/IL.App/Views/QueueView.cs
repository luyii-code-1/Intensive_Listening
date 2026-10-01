using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using IL.App.Views.Dialogs;
using IL.Core.Transcription;

namespace IL.App.Views;

public sealed class QueueView : UserControl
{
    private readonly TranscriptionQueue _queue;
    private readonly Func<TranscriptionJob, Task> _loadSrt;
    private readonly Func<DuplicateMatch, Task>? _openExisting;
    private readonly Func<Task>? _pickAudio;
    private readonly SpringListCollection<Control> _rows;
    private readonly StackPanel _content = new() { Spacing = 10 };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _listening;
    private sealed record JobLayout(TranscriptionJobStatus Status, TranscriptionStage Stage, string Title, bool Consumed, string? Srt, DuplicateMatch? Duplicate, bool Segments);
    private sealed record JobRow(JobLayout Layout, Control Card, TextBlock Message, ProgressBar? Progress, TextBlock? Percentage, TextBlock? Segments);
    private readonly Dictionary<string, JobRow> _jobRows = [];
    private static JobLayout LayoutFor(TranscriptionJob job) => new(job.Status, job.Stage, job.Title, job.SrtConsumed, job.Srt, job.Duplicate, job.SegmentTotal is > 0);
    private static void UpdateProgress(JobRow row, TranscriptionJob job)
    {
        var elapsed = Elapsed(job); row.Message.Text = elapsed == null ? job.Message : $"{job.Message}（{elapsed}）";
        var percentage = (int)Math.Round(Math.Clamp((job.Fraction ?? 0) * 100, 0, 100));
        if (row.Progress != null) row.Progress.Value = percentage;
        if (row.Percentage != null) row.Percentage.Text = $"{percentage}%";
        if (row.Segments != null) row.Segments.Text = $"分段 {job.SegmentIndex ?? 0}/{job.SegmentTotal}";
    }
    public QueueView(TranscriptionQueue queue, Func<TranscriptionJob, Task> loadSrt, Func<DuplicateMatch, Task>? openExisting = null, Func<Task>? pickAudio = null)
    {
        _queue = queue; _loadSrt = loadSrt; _openExisting = openExisting; _pickAudio = pickAudio;
        _rows = new(_content, _content.Children, frame => frame, _content.Children.Move);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = _content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = root;
        _refresh.Tick += (_, _) => { _refresh.Stop(); Render(); };
        AttachedToVisualTree += (_, _) => { if (!_listening) { _queue.Changed += OnChanged; _listening = true; } Render(); };
        DetachedFromVisualTree += (_, _) => { _queue.Changed -= OnChanged; _listening = false; _refresh.Stop(); };
    }
    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (_listening && !_refresh.IsEnabled) _refresh.Start(); });
    private void Render()
    {
        var entries = new List<(string Key, Control Content)>(); var jobs = _queue.Jobs;
        var ids = jobs.Select(j => j.Id).ToHashSet();
        foreach (var id in _jobRows.Keys.Where(id => !ids.Contains(id)).ToArray()) _jobRows.Remove(id);
        if (jobs.Count == 0)
        {
            var empty = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(28, 60), MaxWidth = 500 };
            empty.Children.Add(WorkspaceUi.Icon("sync", 40));
            empty.Children.Add(new TextBlock { Text = "暂无转写任务", FontWeight = FontWeight.SemiBold, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(WorkspaceUi.Text("在教师端项目中提交音频后，可随时从这里查看进度。"));
            if (_pickAudio != null) { var pick = WorkspaceUi.Button("选择音频", () => RunAsync(_pickAudio)); pick.HorizontalAlignment = HorizontalAlignment.Center; empty.Children.Add(pick); }
            _rows.Update([("empty", empty)]); return;
        }
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,*,Auto") };
        header.Children.Add(WorkspaceUi.Text("转写队列", 14, true)); var count = WorkspaceUi.Text($"{jobs.Count} 个任务", 12); count.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(count, 2); header.Children.Add(count);
        if (jobs.Any(j => j.IsTerminal)) { var clear = WorkspaceUi.Button("清除已完成", () => RunAsync(() => { _queue.ClearFinished(); return Task.CompletedTask; })); Grid.SetColumn(clear, 3); header.Children.Add(clear); }
        entries.Add(("header", header));
        var interrupted = jobs.Count(j => j.Status == TranscriptionJobStatus.Interrupted);
        if (interrupted > 0)
            entries.Add(("interrupted", new FAInfoBar { IsOpen = true, IsClosable = false, Severity = FAInfoBarSeverity.Warning, Title = "有未完成的任务", Message = $"上次退出时还有 {interrupted} 个任务没有结束。", ActionButton = WorkspaceUi.Button("继续", () => RunAsync(() => { _queue.ResumeInterrupted(); return Task.CompletedTask; })) }));
        foreach (var job in jobs)
        {
            var layout = LayoutFor(job);
            if (_jobRows.TryGetValue(job.Id, out var existing) && existing.Layout == layout)
            { UpdateProgress(existing, job); entries.Add((job.Id, existing.Card)); continue; }
            ProgressBar? progress = null; TextBlock? percentageLabel = null, segments = null;
            var color = StatusBrush(job);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,12,*") };
            var icon = WorkspaceUi.Icon(StatusIcon(job), 20); icon.Foreground = color; icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 2, 0, 0); row.Children.Add(icon);
            var body = new StackPanel { Spacing = 6 };
            var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") }; var title = WorkspaceUi.Text(job.Title, 14, true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; titleRow.Children.Add(title);
            var label = WorkspaceUi.Text(Status(job), 12); label.Foreground = color; Grid.SetColumn(label, 2); titleRow.Children.Add(label); body.Children.Add(titleRow);
            var elapsed = Elapsed(job); var message = WorkspaceUi.Text(elapsed == null ? job.Message : $"{job.Message}（{elapsed}）", 12); message.Opacity = .85; body.Children.Add(message);
            if (job.Status == TranscriptionJobStatus.Running)
            {
                var percentage = (int)Math.Round(Math.Clamp((job.Fraction ?? 0) * 100, 0, 100));
                var progressRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,42"), Margin = new Thickness(0, 4, 0, 0) };
                progressRow.Children.Add(progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = percentage, Height = 5, VerticalAlignment = VerticalAlignment.Center });
                var value = percentageLabel = WorkspaceUi.Text($"{percentage}%", 12); value.TextAlignment = TextAlignment.Right; Grid.SetColumn(value, 2); progressRow.Children.Add(value); body.Children.Add(progressRow);
            }
            if (job.SegmentTotal is int total && total > 0) body.Children.Add(segments = WorkspaceUi.Text($"分段 {job.SegmentIndex ?? 0}/{total}", 12));
            var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (job.Status == TranscriptionJobStatus.Completed && !job.SrtConsumed && !string.IsNullOrWhiteSpace(job.Srt)) actions.Children.Add(Action("载入字幕", () => _loadSrt(job), true));
            if (job.Status == TranscriptionJobStatus.AwaitingDecision && job.Duplicate != null) actions.Children.Add(Action("处理重复音频", () => ResolveDuplicateAsync(job), true));
            if (job.IsActive) actions.Children.Add(Action("取消", () => { _queue.Cancel(job.Id); return Task.CompletedTask; }));
            if (job.Status is TranscriptionJobStatus.Failed or TranscriptionJobStatus.Canceled or TranscriptionJobStatus.Interrupted) actions.Children.Add(Action("重试", () => { _queue.Retry(job.Id); return Task.CompletedTask; }));
            if (job.Status == TranscriptionJobStatus.Completed && !string.IsNullOrWhiteSpace(job.Srt)) actions.Children.Add(Action("导出 SRT", async () => { var path = await WorkspaceUi.Save(this, "导出转写字幕", job.Title + ".srt", "srt"); if (path != null) await File.WriteAllTextAsync(path, job.Srt); }));
            actions.Children.Add(Action("删除", async () => { if (await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "删除转写任务？", job.IsActive ? $"将取消并永久删除「{job.Title}」。" : $"将永久删除「{job.Title}」的任务记录。", "删除")) { _queue.Delete(job.Id); WorkspaceToast.Show(this, "任务已删除", $"「{job.Title}」已从转写队列移除。"); } }));
            body.Children.Add(actions); Grid.SetColumn(body, 2); row.Children.Add(body); var card = WorkspaceUi.Surface(row, new Thickness(14));
            _jobRows[job.Id] = new(layout, card, message, progress, percentageLabel, segments); entries.Add((job.Id, card));
        }
        _rows.Update(entries);
    }
    private Button Action(string text, Func<Task> action, bool primary = false) { var button = WorkspaceUi.Button(text, () => RunAsync(action), primary); button.Margin = new Thickness(0, 0, 8, 0); return button; }
    public async Task ResolveDuplicateAsync(TranscriptionJob job)
    {
        if (job.Duplicate == null) return;
        var duplicate = job.Duplicate; var choice = await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this), "发现相同音频", $"「{duplicate.ExistingTitle}」已经使用这段音频。", "取消", "仍然转写", duplicate.JobId != null ? "查看已有任务" : "打开已有课程");
        if (choice == 1) _queue.Resolve(job.Id, true); else { _queue.Resolve(job.Id, false); if (choice == 2 && _openExisting != null) await _openExisting(duplicate); }
    }
    private static string? Elapsed(TranscriptionJob job)
    {
        if (job.StartedAt == null || !job.FractionIsEstimated) return null;
        var seconds = (int)(DateTimeOffset.Now - job.StartedAt.Value).TotalSeconds;
        return seconds < 5 ? null : $"已用 {seconds / 60}:{seconds % 60:00}";
    }
    private static string Status(TranscriptionJob job) => job.Status switch
    {
        TranscriptionJobStatus.Queued => "排队中", TranscriptionJobStatus.AwaitingDecision => "等待确认", TranscriptionJobStatus.Completed => "已完成", TranscriptionJobStatus.Failed => "失败", TranscriptionJobStatus.Canceled => "已取消", TranscriptionJobStatus.Interrupted => "已中断",
        _ => job.Stage switch { TranscriptionStage.Queued => "准备中", TranscriptionStage.Fingerprinting => "校验中", TranscriptionStage.Decoding => "解码中", TranscriptionStage.Slicing => "切片中", TranscriptionStage.Uploading => "上传中", TranscriptionStage.Recognizing => "识别中", TranscriptionStage.Formatting => "生成字幕", _ => "合并中" }
    };
    private static string StatusIcon(TranscriptionJob job) => job.Status switch { TranscriptionJobStatus.Completed => "completed", TranscriptionJobStatus.Failed => "error_badge", TranscriptionJobStatus.Canceled => "cancel", TranscriptionJobStatus.Interrupted => "warning", TranscriptionJobStatus.AwaitingDecision => "help", TranscriptionJobStatus.Queued => "clock", _ => "sync" };
    private static IBrush StatusBrush(TranscriptionJob job) => job.Status switch { TranscriptionJobStatus.Completed => Brushes.ForestGreen, TranscriptionJobStatus.Failed => Brushes.Firebrick, TranscriptionJobStatus.Canceled => Brushes.Gray, TranscriptionJobStatus.Interrupted or TranscriptionJobStatus.AwaitingDecision => Brushes.DarkOrange, _ => WorkspaceUi.Accent };
    private async Task RunAsync(Func<Task> action) { try { await action(); Render(); } catch (Exception ex) { WorkspaceToast.Show(this, "转写任务操作失败", ex.Message, true); AppLog(ex); } }
    private static void AppLog(Exception ex) => IL.Core.Infrastructure.AppLog.Error("转写任务操作失败", ex);
}
