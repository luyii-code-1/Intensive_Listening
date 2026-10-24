using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using IL.Core.Telemetry;

namespace IL.App.Services;

internal static class InteractionPerformance
{
    public static AppTelemetry? Telemetry { get; set; }
    public static IDisposable? Measure(string operation) => Telemetry?.StartPerformanceTimer(operation);
    public static void Install(Window window)
    {
        void MeasureFrame(string operation)
        {
            if (Measure(operation) is not { } timer) return;
            window.RequestAnimationFrame(_ => timer.Dispose());
        }
        window.AddHandler(InputElement.PointerPressedEvent, (_, _) => MeasureFrame("input.pointer.frame"), RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, (_, _) => MeasureFrame("input.key.frame"), RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => MeasureFrame("input.scroll.frame"), RoutingStrategies.Tunnel);
    }
}
