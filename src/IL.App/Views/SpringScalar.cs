using System.Diagnostics;
using Avalonia.Threading;

namespace IL.App.Views;

/// <summary>Retargetable scalar spring for local layout changes such as disclosures and the navigation pane.</summary>
internal sealed class SpringScalar(double initial, Action<double> apply)
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private double _value = initial, _velocity, _target = initial, _last, _response;
    private TaskCompletionSource? _completion;
    private bool _initialized;
    public double Value => _value;
    public void Set(double value) { Stop(); _value = _target = value; _velocity = 0; apply(value); }
    public Task To(double target, double response = .32)
    {
        _completion?.TrySetResult(); _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _target = target; _response = response;
        if (!_initialized) { _timer.Tick += (_, _) => Tick(); _initialized = true; }
        if (!_timer.IsEnabled) { _clock.Restart(); _last = 0; _timer.Start(); }
        return _completion.Task;
    }
    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds; var dt = Math.Min(.064, now - _last); _last = now;
        var omega = 2 * Math.PI / _response; var offset = _value - _target; var coefficient = _velocity + omega * offset; var decay = Math.Exp(-omega * dt);
        _value = _target + (offset + coefficient * dt) * decay; _velocity = (_velocity - omega * coefficient * dt) * decay;
        if (Math.Abs(_value - _target) < .05 && Math.Abs(_velocity) < .1) { _value = _target; _velocity = 0; apply(_value); Stop(); }
        else apply(_value);
    }
    public void Stop() { _timer.Stop(); _completion?.TrySetResult(); _completion = null; }
}
