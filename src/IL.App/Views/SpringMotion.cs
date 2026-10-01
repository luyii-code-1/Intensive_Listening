using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using IL.App.Views.Dialogs;

namespace IL.App.Views;

/// <summary>Analytic critically damped springs, retargeted from live position and velocity.</summary>
public sealed class SpringMotion
{
    private sealed class Axis(double initial)
    {
        public double Value = initial, Velocity, Target = initial;
        public bool Step(double seconds, double response)
        {
            var omega = 2 * Math.PI / response;
            var offset = Value - Target; var coefficient = Velocity + omega * offset;
            var decay = Math.Exp(-omega * seconds);
            Value = Target + (offset + coefficient * seconds) * decay;
            Velocity = (Velocity - omega * coefficient * seconds) * decay;
            if (Math.Abs(Value - Target) < .0005 && Math.Abs(Velocity) < .005) { Value = Target; Velocity = 0; return true; }
            return false;
        }
    }
    private readonly Control _control;
    private readonly TransformGroup _group;
    private readonly TranslateTransform _translation = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Axis _opacity = new(1), _x = new(0), _y = new(0), _size = new(1);
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private double _last, _response = .32;
    private TaskCompletionSource? _completion;
    public SpringMotion(Control control)
    {
        _control = control;
        control.RenderTransformOrigin = RelativePoint.Center;
        _group = new TransformGroup { Children = { _scale, _translation } };
        control.RenderTransform = _group;
        _timer.Tick += (_, _) => Tick();
        control.DetachedFromVisualTree += (_, _) => Stop();
    }
    public void Set(double opacity = 1, double x = 0, double y = 0, double scale = 1)
    {
        Stop(); Reset(_opacity, opacity); Reset(_x, x); Reset(_y, y); Reset(_size, scale); Apply();
    }
    private static void Reset(Axis axis, double value) { axis.Value = axis.Target = value; axis.Velocity = 0; }
    public Task To(double opacity = 1, double x = 0, double y = 0, double scale = 1, double response = .32)
    {
        _completion?.TrySetResult(); _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _opacity.Target = opacity; _x.Target = x; _y.Target = y; _size.Target = scale;
        _response = FirstRunWizard.MotionReduced() ? .18 : response;
        if (FirstRunWizard.MotionReduced()) { Reset(_x, 0); Reset(_y, 0); Reset(_size, 1); }
        if (!_timer.IsEnabled) { _clock.Restart(); _last = 0; _timer.Start(); }
        return _completion.Task;
    }
    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds; var elapsed = Math.Min(.064, now - _last); _last = now;
        var done = _opacity.Step(elapsed, _response) & _x.Step(elapsed, _response) & _y.Step(elapsed, _response) & _size.Step(elapsed, _response);
        Apply(); if (done) Stop();
    }
    private void Apply() { _control.RenderTransform = _group; _control.Opacity = _opacity.Value; _translation.X = _x.Value; _translation.Y = _y.Value; _scale.ScaleX = _scale.ScaleY = _size.Value; }
    public Task Pulse()
    {
        if (FirstRunWizard.MotionReduced()) _opacity.Velocity -= .65;
        else _size.Velocity -= .45;
        return To(response: .28);
    }
    public void Stop() { _timer.Stop(); _completion?.TrySetResult(); _completion = null; }
    public static SpringMotion Entrance(Control control, double y = 12, double scale = 1)
    {
        var motion = For(control); motion.Set(0, y: FirstRunWizard.MotionReduced() ? 0 : y, scale: FirstRunWizard.MotionReduced() ? 1 : scale);
        control.AttachedToVisualTree += (_, _) => _ = motion.To(); return motion;
    }
    private static readonly ConditionalWeakTable<Control, SpringMotion> Motions = new();
    public static SpringMotion For(Control control) => Motions.GetValue(control, c => new SpringMotion(c));
    private static bool _installed;
    public static void InstallFeedback()
    {
        if (_installed) return; _installed = true;
        Control.LoadedEvent.AddClassHandler<Expander>((expander, args) => expander.ContentTransition = new SpringDisclosureTransition());
        ListBoxItem.IsSelectedProperty.Changed.AddClassHandler<ListBoxItem>((item, args) =>
        { if (TopLevel.GetTopLevel(item) != null && item.IsSelected) _ = For(item).Pulse(); });
        Button.IsPressedProperty.Changed.AddClassHandler<Button>((button, args) =>
        {
            if (TopLevel.GetTopLevel(button) == null) return;
            var motion = For(button);
            _ = motion.To(opacity: button.IsPressed ? .88 : 1, scale: button.IsPressed ? .97 : 1, response: .22);
        });
        ToggleButton.IsCheckedProperty.Changed.AddClassHandler<ToggleButton>((button, args) =>
        {
            if (TopLevel.GetTopLevel(button) == null || button.IsPressed) return;
            var motion = For(button);
            // Press/release and selection share a spring rather than restarting a keyframe.
            _ = motion.Pulse();
        });
    }
}
