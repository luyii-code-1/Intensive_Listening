using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;
using IL.App.Views.Dialogs;

namespace IL.App.Views;

public sealed class WorkspacePageTransition : IPageTransition
{
    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (from is not null) from.IsVisible = false;
        if (to is null) return;
        to.IsVisible = true;
        if (FirstRunWizard.MotionReduced()) return;
        var transform = to.RenderTransform;
        to.RenderTransform = new TranslateTransform(0, 18);
        to.Opacity = 0;
        try
        {
            await new Animation
            {
                Duration = TimeSpan.FromMilliseconds(250), Easing = new CubicEaseOut(), FillMode = FillMode.None,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 18d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
                }
            }.RunAsync(to, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) { }
        finally { to.Opacity = 1; to.RenderTransform = transform; }
    }
}
