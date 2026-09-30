using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using IL.App.Views.Dialogs;
using IL.Core.Transcription;

namespace IL.App.Views;

public sealed class QueueView : UserControl
{
    private readonly TranscriptionQueue _queue;
    private readonly Func<TranscriptionJob, Task> _loadSrt;
    private readonly Func<DuplicateMatch, Task>? _openExisting;
    private readonly StackPanel _jobs = new() { Spacing = 10 };
    private readonly TextBlock _summary = WorkspaceUi.Text("", 16, true), _status = WorkspaceUi.Text("");
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _listening;
    public QueueView(TranscriptionQueue queue, Func<TranscriptionJob, Task> loadSrt, Func<DuplicateMatch, Task>? openExisting = null, Func<Task>? pickAudio = null)
    {
        _queue = queue; _loadSrt = loadSrt; _openExisting = openExisting;
        var toolbar = WorkspaceUi.Row(_summary, WorkspaceUi.Button("继续中断任务", () => RunAsync(() => { _queue.ResumeInterrupted(); return Task.CompletedTask; })),
            WorkspaceUi.Button("清除已结束任务", () => RunAsync(() => { _queue.ClearFinished(); return Task.CompletedTask; })));
        if (pickAudio != null) toolbar.Children.Add(WorkspaceUi.Button("选择音频", () => RunAsync(pickAudio), true));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(toolbar); var scroll = new ScrollViewer { Content = _jobs }; Grid.SetRow(scroll, 1); root.Children.Add(scroll); Grid.SetRow(_status, 2); root.Children.Add(_status);
        Content = root; Padding = new Thickness(24);
        _refresh.Tick += (_, _) => { _refresh.Stop(); Render(); };
        AttachedToVisualTree += (_, _) => { if (!_listening) { _queue.Changed += OnChanged; _listening = true; } Render(); };
        DetachedFromVisualTree += (_, _) => { _queue.Changed -= OnChanged; _listening = false; _refresh.Stop(); };
    }
    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_refresh.IsEnabled) _refresh.Start(); });
    private void Render()
    {
        _jobs.Children.Clear(); var jobs = _queue.Jobs;
        _summary.Text = $"转写队列 · {jobs.Count} 个任务";
        if (jobs.Count == 0) { _jobs.Children.Add(WorkspaceUi.Text("在制作工程中提交音频后，可在这里查看进度。")); return; }
        var interrupted = jobs.Count(j => j.Status == TranscriptionJobStatus.Interrupted);
        if (interrupted > 0) _jobs.Children.Add(WorkspaceUi.Text($"上次退出时有 {interrupted} 个任务尚未完成，可以继续处理。"));
        foreach (var job in jobs)
        {
            var panel = WorkspaceUi.Stack(WorkspaceUi.Row(WorkspaceUi.Text(job.Title, 16, true), WorkspaceUi.Text(Status(job))), WorkspaceUi.Text(job.Message));
            if (job.IsActive)
            {
                panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp((job.Fraction ?? 0) * 100, 0, 100), IsIndeterminate = job.Fraction == null, Height = 5 });
                panel.Children.Add(WorkspaceUi.Text($"{job.Stage} · {(job.Fraction is double fraction ? $"{fraction * 100:0}%" : "处理中")}" + (job.SegmentTotal is int total ? $" · 分段 {job.SegmentIndex ?? 0}/{total}" : ""), 12));
            }
            if (job.StartedAt is DateTimeOffset start) panel.Children.Add(WorkspaceUi.Text($"开始于 {start.ToLocalTime():HH:mm:ss} · 用时 {(job.FinishedAt ?? DateTimeOffset.Now) - start:mm\\:ss}" + (job.FractionIsEstimated ? " · 进度为估计值" : ""), 12));
            var actions = new WrapPanel();
            if (job.Status == TranscriptionJobStatus.AwaitingDecision && job.Duplicate != null)
                actions.Children.Add(WorkspaceUi.Button("处理重复音频", () => RunAsync(() => ResolveDuplicateAsync(job)), true));
            if (job.Status == TranscriptionJobStatus.Completed && !string.IsNullOrWhiteSpace(job.Srt))
            {
                actions.Children.Add(WorkspaceUi.Button(job.SrtConsumed ? "打开工程" : "载入字幕", () => RunAsync(() => _loadSrt(job)), !job.SrtConsumed));
                actions.Children.Add(WorkspaceUi.Button("导出 SRT", () => RunAsync(async () =>
                { var path = await WorkspaceUi.Save(this, "导出转写字幕", job.Title + ".srt", "srt"); if (path != null) await File.WriteAllTextAsync(path, job.Srt); })));
            }
            if (job.IsActive) actions.Children.Add(WorkspaceUi.Button("取消", () => RunAsync(() => { _queue.Cancel(job.Id); return Task.CompletedTask; })));
            if (job.Status is TranscriptionJobStatus.Failed or TranscriptionJobStatus.Canceled or TranscriptionJobStatus.Interrupted)
                actions.Children.Add(WorkspaceUi.Button("重试", () => RunAsync(() => { _queue.Retry(job.Id); return Task.CompletedTask; })));
            actions.Children.Add(WorkspaceUi.Button("删除", () => RunAsync(async () =>
            { if (await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "删除转写任务", job.IsActive ? $"取消并删除「{job.Title}」的任务记录。" : $"删除「{job.Title}」的任务记录。", "删除")) _queue.Delete(job.Id); })));
            panel.Children.Add(actions); _jobs.Children.Add(WorkspaceUi.Surface(panel));
        }
    }
    public async Task ResolveDuplicateAsync(TranscriptionJob job)
    {
        if (job.Duplicate == null) return;
        var duplicate = job.Duplicate;
        var choice = await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this), "发现相同音频", $"「{duplicate.ExistingTitle}」已经使用这段音频。", "取消", "仍然转写", duplicate.JobId != null ? "查看已有任务" : "打开已有课程");
        if (choice == 1) _queue.Resolve(job.Id, true);
        else { _queue.Resolve(job.Id, false); if (choice == 2 && _openExisting != null) await _openExisting(duplicate); }
    }
    private static string Status(TranscriptionJob job) => job.Status switch
    { TranscriptionJobStatus.Queued => "排队中", TranscriptionJobStatus.Running => "转写中", TranscriptionJobStatus.AwaitingDecision => "等待确认", TranscriptionJobStatus.Completed => "已完成", TranscriptionJobStatus.Failed => "失败", TranscriptionJobStatus.Canceled => "已取消", _ => "已中断" };
    private async Task RunAsync(Func<Task> action) { try { await action(); Render(); } catch (Exception ex) { _status.Text = ex.Message; } }
}
