using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using IL.App.Views.Dialogs;

namespace IL.App.Views;

internal sealed class SpringScroll
{
    private static readonly ConditionalWeakTable<ScrollViewer, SpringScroll> Motions = new();
    private readonly ScrollViewer _viewer;
    private readonly SpringScalar _x, _y;
    private bool _applying, _moving, _wheelInstalled, _wheelMoving, _playerScroll;
    private Vector _target;
    private long _generation;
    private Task _settled = Task.CompletedTask;
    private SpringScroll(ScrollViewer viewer)
    {
        _viewer = viewer;
        _x = new(viewer, viewer.Offset.X, value => Apply(new(value, viewer.Offset.Y)), AnimationDisabled);
        _y = new(viewer, viewer.Offset.Y, value => Apply(new(viewer.Offset.X, value)), AnimationDisabled);
        // Offset changes are synchronous; ScrollChanged is delivered later during layout,
        // when rounded/coerced offsets can otherwise cancel our own animation frames.
        viewer.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty && !_applying
                && (!_moving || (_viewer.Offset - Clamp(new Vector(_x.Value, _y.Value))).Length > 1)) Stop();
        };
        viewer.DetachedFromVisualTree += (_, _) => Stop();
        viewer.AddHandler(InputElement.PointerPressedEvent, (_, _) => Stop(), RoutingStrategies.Tunnel);
    }
    public static SpringScroll For(ScrollViewer viewer) => Motions.GetValue(viewer, v => new(v));
    public static SpringScroll ForPlayer(ScrollViewer viewer)
    {
        var scroll = For(viewer);
        scroll._playerScroll = true;
        return scroll;
    }
    private bool AnimationDisabled() => MotionPreferences.PerformanceMode || (!_playerScroll && MotionPreferences.ReducedMotion);
    private Vector Clamp(Vector target) => new(
        Math.Clamp(target.X, 0, Math.Max(0, _viewer.Extent.Width - _viewer.Viewport.Width)),
        Math.Clamp(target.Y, 0, Math.Max(0, _viewer.Extent.Height - _viewer.Viewport.Height)));
    private void Apply(Vector value)
    {
        _applying = true;
        try { _viewer.Offset = Clamp(value); }
        finally { _applying = false; }
    }
    public void Stop() { ++_generation; _moving = _wheelMoving = false; _x.Stop(); _y.Stop(); }
    public Task To(Vector target) => To(target, wheel: false);
    private Task To(Vector target, bool wheel)
    {
        target = Clamp(target);
        if (_moving && target == _target || !_moving && target == _viewer.Offset) return _settled;
        if (!_moving) { _x.Set(_viewer.Offset.X); _y.Set(_viewer.Offset.Y); }
        _target = target;
        if (AnimationDisabled() || FirstRunWizard.SystemMotionReduced()) { Stop(); Apply(target); return Task.CompletedTask; }
        _moving = true;
        _wheelMoving = wheel;
        var response = _playerScroll && (wheel || MotionPreferences.ReducedMotion) ? .22 : .42;
        return _settled = CompleteAsync(++_generation, _x.To(target.X, response), _y.To(target.Y, response));
    }
    private async Task CompleteAsync(long generation, Task x, Task y)
    {
        await Task.WhenAll(x, y);
        if (_generation == generation) _moving = _wheelMoving = false;
    }
    public static void Reveal(Control control)
    {
        var viewer = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null || control.TranslatePoint(default, viewer) is not { } point) return;
        var offset = viewer.Offset;
        var x = point.X < 0 ? offset.X + point.X : point.X + control.Bounds.Width > viewer.Viewport.Width
            ? offset.X + point.X + control.Bounds.Width - viewer.Viewport.Width : offset.X;
        var y = point.Y < 0 ? offset.Y + point.Y : point.Y + control.Bounds.Height > viewer.Viewport.Height
            ? offset.Y + point.Y + control.Bounds.Height - viewer.Viewport.Height : offset.Y;
        For(viewer).To(new(x, y));
    }
    public static async Task RevealIndexAsync(ListBox list, int index)
    {
        if (index < 0 || index >= list.Items.Count) return;
        if (list.ContainerFromIndex(index) is { } visible) { Reveal(visible); return; }
        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null) return;
        var motion = For(viewer);
        motion.Stop();
        var previous = viewer.Offset;
        // Resolve the virtualized destination during this layout pass, then animate before the next frame.
        list.ScrollIntoView(index); list.UpdateLayout();
        var destination = viewer.Offset;
        viewer.Offset = previous;
        var scroll = motion.To(destination);
        var generation = motion._generation;
        await scroll;
        if (motion._generation == generation && list.ContainerFromIndex(index) is { } realized) Reveal(realized);
    }
    public static void Install()
    {
        Control.LoadedEvent.AddClassHandler<ScrollViewer>((viewer, _) =>
        {
            var motion = For(viewer);
            if (motion._wheelInstalled) return;
            motion._wheelInstalled = true;
            viewer.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
            {
                if (e.Handled) return;
                if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source).OfType<ScrollViewer>().FirstOrDefault() != viewer) return;
                // Player surfaces use pixel offsets. Other viewers may scroll in item units.
                var step = motion._playerScroll ? new Size(120, 120) : viewer.GetVisualDescendants().OfType<ILogicalScrollable>()
                    .FirstOrDefault(c => c.IsLogicalScrollEnabled)?.ScrollSize ?? new Size(48, 48);
                var delta = e.Delta;
                if (motion._playerScroll && delta.X == 0 && (e.KeyModifiers == KeyModifiers.Shift || viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled))
                    delta = new Vector(delta.Y, 0);
                var precise = motion._playerScroll && (delta.X != Math.Truncate(delta.X) || delta.Y != Math.Truncate(delta.Y));
                if (motion._playerScroll && motion._moving && (precise || !motion._wheelMoving)) motion.Stop();
                var start = motion._moving ? motion._target : viewer.Offset;
                var target = motion.Clamp(new(start.X - delta.X * step.Width, start.Y - delta.Y * step.Height));
                if (target == start) return;
                e.Handled = true;
                // Apply fractional wheel deltas immediately so precision input follows the gesture.
                if (precise) motion.Apply(target);
                else motion.To(target, wheel: true);
            }, RoutingStrategies.Tunnel);
        });
    }
}
