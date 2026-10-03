using Avalonia.Controls;

namespace IL.App.Views;

/// <summary>Retargetable scalar spring for local layout changes such as disclosures and the navigation pane.</summary>
internal sealed class SpringScalar(Control owner, double initial, Action<double> apply)
{
    private double _value = initial, _velocity, _target = initial, _response;
    private TimeSpan? _lastFrame;
    private bool _running;
    private long _generation;
    private TaskCompletionSource? _completion;
    public double Value => _value;
    public void Set(double value) { Stop(); _value = _target = value; _velocity = 0; apply(value); }
    public Task To(double target, double response = .32)
    {
        _completion?.TrySetResult(); _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _target = target; _response = response;
        var completion = _completion;
        if (!_running)
        {
            if (TopLevel.GetTopLevel(owner) is not { } topLevel) { Set(target); return completion.Task; }
            _running = true; _lastFrame = null;
            RequestFrame(topLevel, ++_generation);
        }
        return completion.Task;
    }
    private void RequestFrame(TopLevel topLevel, long generation) => topLevel.RequestAnimationFrame(time =>
    {
        if (!_running || generation != _generation) return;
        var dt = _lastFrame is { } last ? Math.Max(0, (time - last).TotalSeconds) : 0;
        _lastFrame = time;
        var omega = 2 * Math.PI / _response; var offset = _value - _target; var coefficient = _velocity + omega * offset; var decay = Math.Exp(-omega * dt);
        _value = _target + (offset + coefficient * dt) * decay; _velocity = (_velocity - omega * coefficient * dt) * decay;
        if (Math.Abs(_value - _target) < .05 && Math.Abs(_velocity) < .1) { _value = _target; _velocity = 0; apply(_value); Stop(); }
        else { apply(_value); RequestFrame(topLevel, generation); }
    });
    public void Stop() { _running = false; ++_generation; _lastFrame = null; _completion?.TrySetResult(); _completion = null; }
}
