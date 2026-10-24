using System.Diagnostics;
using System.Text.Json;
using IL.Core.Infrastructure;

namespace IL.Core.Telemetry;

public sealed partial class AppTelemetry
{
    private readonly object _performanceSync = new();
    private readonly List<PerformanceSample> _performanceSamples = [];
    private bool _performanceEnabled;
    private long _performanceGeneration;
    private sealed record PerformanceSample(string Operation, double DurationMs);

    public void ConfigurePerformanceAnalysis(bool enabled)
    {
        lock (_performanceSync)
        {
            if (_performanceEnabled == enabled) return;
            _performanceEnabled = enabled; ++_performanceGeneration; _performanceSamples.Clear();
        }
    }
    public IDisposable? StartPerformanceTimer(string operation)
    {
        lock (_performanceSync)
            return _performanceEnabled && CanSend ? new PerformanceTimer(this, operation, _performanceGeneration) : null;
    }
    private sealed class PerformanceTimer(AppTelemetry owner, string operation, long generation) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.RecordPerformance(operation, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, generation);
        }
    }
    private void RecordPerformance(string operation, double elapsed, long generation)
    {
        PerformanceSample[] batch;
        lock (_performanceSync)
        {
            if (!_performanceEnabled || !CanSend || generation != _performanceGeneration) return;
            _performanceSamples.Add(new(operation, Math.Round(elapsed, 2)));
            if (_performanceSamples.Count < 500 && elapsed <= 2000) return;
            batch = _performanceSamples.ToArray(); _performanceSamples.Clear();
        }
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                lock (_performanceSync)
                    if (!_performanceEnabled || !CanSend || generation != _performanceGeneration) return;
                await _transport.EventAsync("track_timer", new Dictionary<string, string>
                {
                    ["samples"] = JsonSerializer.Serialize(batch, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                });
            }
            catch (Exception error) { AppLog.Warning("性能分析上报失败", error); }
            finally { _gate.Release(); }
        });
    }
}
