using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using IL.App.Views.Dialogs;
using IL.Core.Mcp;

namespace IL.App.Views;

public sealed class AgentProgressView : UserControl
{
    private readonly AgentWorkflowProgress _progress;
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 10, Foreground = WorkspaceUi.Accent };
    private readonly TextBlock _summary = WorkspaceUi.Text("", 12);
    private readonly StackPanel _steps = new() { Spacing = 12, Margin = new Thickness(0, 0, 20, 0) };
    public AgentProgressView(AgentWorkflowProgress progress)
    {
        _progress = progress;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(_bar); content.Children.Add(_summary);
        content.Children.Add(new ScrollViewer
        {
            Content = _steps, MaxHeight = 280, Margin = new Thickness(0, 4, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Content = content;
        AttachedToVisualTree += (_, _) => { _progress.Changed += OnChanged; Render(); };
        DetachedFromVisualTree += (_, _) => _progress.Changed -= OnChanged;
        Render();
    }
    private void OnChanged() => Dispatcher.UIThread.Post(Render);
    private void Render()
    {
        var snapshot = _progress.Snapshot;
        _bar.Value = snapshot.Percent;
        _summary.Text = $"{snapshot.Percent}% · 已完成 {snapshot.CompletedSteps} / {snapshot.Steps.Count} 步";
        _steps.Children.Clear();
        foreach (var step in snapshot.Steps)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            var number = WorkspaceUi.Text(step.Number.ToString(), 12); number.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(number);
            var title = WorkspaceUi.Text(step.Title, 14, step.Status == "running"); Grid.SetColumn(title, 1); row.Children.Add(title);
            var status = WorkspaceUi.Text(step.Status switch
            {
                "running" => "进行中", "completed" => "已完成", "skipped" => "已跳过", "failed" => "失败", _ => "等待"
            }, 12);
            status.Margin = new Thickness(12, 0, 0, 0); status.VerticalAlignment = VerticalAlignment.Center;
            if (step.Status == "running") status.Foreground = WorkspaceUi.Accent;
            if (step.Status == "failed") status.Foreground = Brushes.IndianRed;
            Grid.SetColumn(status, 2); row.Children.Add(status);
            var detail = WorkspaceUi.Text(string.IsNullOrWhiteSpace(step.Detail) ? step.Description : step.Detail, 12);
            detail.Opacity = .75; detail.Margin = new Thickness(0, 3, 0, 0);
            Grid.SetRow(detail, 1); Grid.SetColumn(detail, 1); Grid.SetColumnSpan(detail, 2); row.Children.Add(detail);
            if (step.Status == "pending") row.Opacity = .6;
            _steps.Children.Add(row);
        }
    }
}
