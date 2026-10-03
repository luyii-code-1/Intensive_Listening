using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using IL.App.Views.Dialogs;

namespace IL.App.Views;

public sealed class SpringDisclosureTransition : IPageTransition
{
    private sealed class State(Control control)
    {
        public readonly SpringMotion Motion = SpringMotion.For(control);
        public readonly SpringScalar Height = new(control, control.Bounds.Height, h => control.Height = Math.Max(0, h));
        public long Generation;
    }
    private readonly ConditionalWeakTable<Control, State> _states = new();
    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if ((to ?? from) is not Control control) return;
        var state = _states.GetValue(control, c => new(c)); var generation = ++state.Generation;
        control.ClipToBounds = true; control.IsVisible = true;
        var opening = to != null; var target = 0d;
        if (opening)
        {
            control.ClearValue(Control.HeightProperty);
            var width = control.Bounds.Width > 0 ? control.Bounds.Width : control.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Bounds.Width > 0)?.Bounds.Width ?? 1;
            control.Measure(new Size(Math.Max(1, width - control.Margin.Left - control.Margin.Right), double.PositiveInfinity)); target = control.DesiredSize.Height;
        }
        control.Height = state.Height.Value;
        using var cancellation = cancellationToken.Register(() => { state.Motion.Stop(); state.Height.Stop(); });
        await Task.WhenAll(state.Motion.To(opacity: opening ? 1 : 0), state.Height.To(target, FirstRunWizard.MotionReduced() ? .22 : .32));
        if (generation != state.Generation || cancellationToken.IsCancellationRequested) return;
        control.IsVisible = opening;
        control.ClearValue(Control.HeightProperty);
    }
}
