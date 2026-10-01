using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using IL.App.Views.Dialogs;
using FluentAvalonia.UI.Controls;

namespace IL.App.Views;

/// <summary>Window-wide, bottom-right InfoBar notifications, following ClassIsland 2's toast presentation.</summary>
public sealed class WorkspaceToast : UserControl
{
    private static readonly ConditionalWeakTable<TopLevel, WorkspaceToast> Hosts = new();
    private readonly StackPanel _messages = new() { Spacing = 4 };
    private readonly List<IDisposable> _timers = [];
    public static TimeSpan SuccessDuration => TimeSpan.FromSeconds(5);
    public static TimeSpan ErrorDuration => TimeSpan.FromSeconds(15);

    private WorkspaceToast(TopLevel owner)
    {
        Name = "WorkspaceToastHost";
        HorizontalAlignment = HorizontalAlignment.Right; VerticalAlignment = VerticalAlignment.Bottom;
        MaxWidth = 400; Margin = new Thickness(4); ZIndex = 50;
        Content = new ScrollViewer { Content = _messages, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        void Resize() => MaxHeight = owner.ClientSize.Height * .65;
        Resize(); owner.SizeChanged += (_, _) => Resize();
        owner.Closed += (_, _) => { foreach (var timer in _timers) timer.Dispose(); _timers.Clear(); };
    }

    public static void Show(Control origin, string message)
    {
        var split = message.Split('\n', 2);
        Show(origin, split.Length == 2 ? split[0] : "", split.Length == 2 ? split[1] : message);
    }

    public static void Show(Control origin, string title, string message, bool error = false)
    {
        if (TopLevel.GetTopLevel(origin) is not { } owner) return;
        if (!Hosts.TryGetValue(owner, out var host))
        {
            host = new WorkspaceToast(owner);
            if (owner.Content is Panel panel)
            {
                if (panel is Grid grid) { Grid.SetColumnSpan(host, Math.Max(1, grid.ColumnDefinitions.Count)); Grid.SetRowSpan(host, Math.Max(1, grid.RowDefinitions.Count)); }
                panel.Children.Add(host);
            }
            else
            {
                var previous = owner.Content; owner.Content = null;
                var root = new Grid(); if (previous is Control content) root.Children.Add(content);
                root.Children.Add(host); owner.Content = root;
            }
            Hosts.Add(owner, host);
        }
        host.Add(title, message, error);
    }

    private void Add(string title, string message, bool error)
    {
        var bar = new FAInfoBar
        {
            Title = title, Message = message, IsOpen = true, IsClosable = true,
            Severity = error ? FAInfoBarSeverity.Error : FAInfoBarSeverity.Success,
            HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 400
        };
        var container = new Border { Child = bar };
        var motion = SpringMotion.Entrance(container, 12, .99);
        var closing = false;
        IDisposable? deadline = null;
        void Close()
        {
            if (closing) return; closing = true; deadline?.Dispose(); if (deadline != null) _timers.Remove(deadline);
            bar.IsEnabled = false;
            _ = RemoveAsync();
        }
        async Task RemoveAsync()
        {
            await motion.To(0, y: 12, scale: .99, response: .22);
            _messages.Children.Remove(container);
        }
        bar.CloseButtonClick += (_, _) => Close();
        // Keep the bar visible while the outer container plays its fade-out.
        bar.Closing += (_, args) => { args.Cancel = true; Close(); };
        if (error)
        {
            bar.ActionButton = WorkspaceUi.IconButton("copy", "复制错误详情", async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync($"{title}\n{message}");
            });
        }
        _messages.Children.Insert(0, container);
        deadline = Schedule(Close, error ? ErrorDuration : SuccessDuration);
    }

    private IDisposable Schedule(Action action, TimeSpan delay)
    {
        IDisposable? timer = null;
        timer = DispatcherTimer.RunOnce(() => { if (timer != null) _timers.Remove(timer); action(); }, delay);
        _timers.Add(timer); return timer;
    }
}
