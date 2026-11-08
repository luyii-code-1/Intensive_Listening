using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using IL.Core.Telemetry;
using IL.Core.Infrastructure;
using IL.App.Views;

namespace IL.App.Services;

internal static class InteractionPerformance
{
    public static AppTelemetry? Telemetry { get; set; }
    public static IDisposable? Measure(string operation) => Telemetry?.StartPerformanceTimer(operation);
    public static UiPerformanceSnapshot Counters { get; private set; } = new(0, 0, 0, 0);
    public static event Action? CountersChanged;
    public static void Install(Window window, Func<Task<bool>> reduceAnimations)
    {
        var clock = Stopwatch.StartNew();
        var counter = new UiStallCounter();
        var pending = 0;
        var active = 0;
        var epoch = 0;
        var closed = false;
        var reducing = false;
        void ResetActivity()
        {
            Interlocked.Increment(ref epoch);
            Volatile.Write(ref active, window.IsActive && window.WindowState != WindowState.Minimized ? 1 : 0);
            counter.ResetDetection(clock.Elapsed);
        }
        void MotionChanged() => counter.ResetDetection(clock.Elapsed, resetCooldown: !reducing);
        MotionPreferences.LevelChanged += MotionChanged;
        window.Activated += (_, _) => ResetActivity();
        window.Deactivated += (_, _) => ResetActivity();
        window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) ResetActivity(); };
        var timer = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref active) == 0 || Interlocked.Exchange(ref pending, 1) != 0) return;
            var sent = clock.Elapsed;
            var sampleEpoch = Volatile.Read(ref epoch);
            Dispatcher.UIThread.Post(async () =>
            {
                Interlocked.Exchange(ref pending, 0);
                if (closed || sampleEpoch != epoch || Volatile.Read(ref active) == 0) return;
                var stalled = counter.Record(clock.Elapsed, clock.Elapsed - sent);
                Counters = counter.Snapshot;
                CountersChanged?.Invoke();
                if (!stalled || reducing) return;
                reducing = true;
                try { await reduceAnimations(); }
                catch (Exception error) { AppLog.Warning("自动调整动画失败", error); }
                finally { reducing = false; }
            }, DispatcherPriority.Normal);
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        window.Closed += (_, _) => { closed = true; Volatile.Write(ref active, 0); timer.Dispose(); MotionPreferences.LevelChanged -= MotionChanged; };
        void MeasureFrame(string operation)
        {
            if (Measure(operation) is not { } frameTimer) return;
            window.RequestAnimationFrame(_ => frameTimer.Dispose());
        }
        window.AddHandler(InputElement.PointerPressedEvent, (_, _) => MeasureFrame("input.pointer.frame"), RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, (_, _) => MeasureFrame("input.key.frame"), RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => MeasureFrame("input.scroll.frame"), RoutingStrategies.Tunnel);
    }
}
