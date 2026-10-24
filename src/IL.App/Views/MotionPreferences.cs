using IL.Core.Settings;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using System.Runtime.CompilerServices;

namespace IL.App.Views;

internal static class MotionPreferences
{
    private sealed class TransitionState(Control control)
    {
        public readonly WeakReference<Control> Control = new(control);
        public IDisposable? Override;
    }
    private static readonly ConditionalWeakTable<Control, TransitionState> States = new();
    private static readonly List<WeakReference<TransitionState>> Loaded = [];
    public static void Install()
    {
        Control.LoadedEvent.AddClassHandler<Control>((control, _) =>
        {
            var state = States.GetValue(control, c =>
            {
                var created = new TransitionState(c); Loaded.Add(new(created)); return created;
            });
            ApplyTransitions(state);
        });
    }
    private static void ApplyTransitions(TransitionState state)
    {
        if (!state.Control.TryGetTarget(out var control)) return;
        if (PerformanceMode) state.Override ??= control.SetValue(Animatable.TransitionsProperty, null, BindingPriority.Animation);
        else { state.Override?.Dispose(); state.Override = null; }
    }
    public static bool PerformanceMode { get; private set; }
    public static bool DisableAnimationWaiting { get; private set; }
    public static void Apply(AppSettings settings)
    {
        var changed = PerformanceMode != settings.PerformanceMode;
        PerformanceMode = settings.PerformanceMode;
        DisableAnimationWaiting = settings.DisableAnimationWaiting;
        if (changed)
            for (var i = Loaded.Count - 1; i >= 0; i--)
                if (Loaded[i].TryGetTarget(out var state)) ApplyTransitions(state);
                else Loaded.RemoveAt(i);
    }
}
