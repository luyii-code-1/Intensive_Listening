namespace IL.Core.Telemetry;

public sealed record UiPerformanceSnapshot(long Samples, long Stalls, double LastDelayMs, double PeakDelayMs);

/// <summary>Local UI-dispatch latency samples; all times use one monotonic clock.</summary>
public sealed class UiStallCounter
{
    private readonly Queue<TimeSpan> _recent = new();
    private TimeSpan _warmUntil = TimeSpan.FromSeconds(10), _coolUntil;
    public UiPerformanceSnapshot Snapshot { get; private set; } = new(0, 0, 0, 0);

    public void ResetDetection(TimeSpan now, bool resetCooldown = false)
    {
        _recent.Clear();
        if (resetCooldown) _coolUntil = now;
        _warmUntil = now + TimeSpan.FromSeconds(10);
    }

    public bool Record(TimeSpan now, TimeSpan delay)
    {
        var ms = Math.Max(0, delay.TotalMilliseconds);
        Snapshot = new(Snapshot.Samples + 1, Snapshot.Stalls + (ms > 100 ? 1 : 0), ms, Math.Max(ms, Snapshot.PeakDelayMs));
        while (_recent.TryPeek(out var sample) && now - sample > TimeSpan.FromSeconds(10)) _recent.Dequeue();
        if (now < _warmUntil || now < _coolUntil) return false;
        if (ms > 100) _recent.Enqueue(now);
        if (_recent.Count < 3) return false;
        _recent.Clear();
        _coolUntil = now + TimeSpan.FromSeconds(30);
        return true;
    }
}
