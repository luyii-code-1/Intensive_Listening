using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace IL.App.Views.Dialogs;

/// <summary>Sound pulses resolve into the Intensive Listening wordmark at first launch.</summary>
internal sealed class WelcomeIntro : Grid
{
    private readonly List<Border> _pulses = new();
    private readonly List<TextBlock> _letters = new();
    private readonly StackPanel _wordmark;

    internal WelcomeIntro()
    {
        Name = "OobeIntro"; IsHitTestVisible = false;
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = new ScaleTransform(1.25, 1.25);
        var pulses = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _wordmark = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        const string title = "Intensive Listening";
        for (var i = 0; i < title.Length; i++)
        {
            var height = 16 + 30 * Math.Pow(Math.Sin((i + 1) * Math.PI / 6), 2);
            var pulse = new Border
            {
                Width = 24, Height = height, CornerRadius = new CornerRadius(4), Background = WorkspaceUi.Accent, Opacity = 0,
                VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = RelativePoint.Center,
                RenderTransform = new TransformGroup { Children = { new TranslateTransform(0, 50), new Rotate3DTransform() } },
            };
            var letter = WorkspaceUi.Text(title[i].ToString(), 32);
            letter.FontWeight = FontWeight.Medium; letter.MinWidth = 24; letter.Opacity = 0; letter.TextAlignment = TextAlignment.Center;
            letter.RenderTransformOrigin = RelativePoint.Center; letter.RenderTransform = new Rotate3DTransform { AngleX = -90 };
            pulses.Children.Add(pulse); _pulses.Add(pulse);
            _wordmark.Children.Add(letter); _letters.Add(letter);
        }
        Children.Add(pulses); Children.Add(_wordmark);
    }

    internal async Task PlayAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var animations = new List<Task>();
        animations.Add(new Animation
        {
            Duration = TimeSpan.FromSeconds(3), Easing = new CubicEaseOut(), FillMode = FillMode.Forward,
            Children =
            {
                Frame(0, new Setter(ScaleTransform.ScaleXProperty, 1.25), new Setter(ScaleTransform.ScaleYProperty, 1.25)),
                Frame(1, new Setter(ScaleTransform.ScaleXProperty, 1d), new Setter(ScaleTransform.ScaleYProperty, 1d)),
            }
        }.RunAsync(this, cancellationToken: cancellation));
        for (var i = 0; i < _pulses.Count; i++)
        {
            var delay = Math.Sin((double)(i + 2) / (_pulses.Count + 2) * Math.PI / 2) * 500 / _pulses.Count;
            var rise = delay * 9;
            var flipAt = rise + 750;
            var end = rise * 2 + 750;
            animations.Add(new Animation
            {
                Duration = TimeSpan.FromMilliseconds(end), FillMode = FillMode.Forward, Easing = new CubicEaseOut(),
                Children =
                {
                    At(0, new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 50d), new Setter(Rotate3DTransform.AngleXProperty, 0d)),
                    At(rise, new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d)),
                    At(flipAt, new Setter(OpacityProperty, 1d), new Setter(Rotate3DTransform.AngleXProperty, 0d)),
                    At(end, new Setter(OpacityProperty, 0d), new Setter(Rotate3DTransform.AngleXProperty, 90d)),
                }
            }.RunAsync(_pulses[i], cancellationToken: cancellation));
            animations.Add(new Animation
            {
                Duration = TimeSpan.FromMilliseconds(end), FillMode = FillMode.Forward, Easing = new CubicEaseOut(),
                Children =
                {
                    At(0, new Setter(OpacityProperty, 0d), new Setter(Rotate3DTransform.AngleXProperty, -90d)),
                    At(flipAt, new Setter(OpacityProperty, 0d), new Setter(Rotate3DTransform.AngleXProperty, -90d)),
                    At(end, new Setter(OpacityProperty, 1d), new Setter(Rotate3DTransform.AngleXProperty, 0d)),
                }
            }.RunAsync(_letters[i], cancellationToken: cancellation));
            await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellation);
        }
        await Task.WhenAll(animations);
        cancellation.ThrowIfCancellationRequested();
        var settle = new List<Task>
        {
            new Animation
            {
                Duration = TimeSpan.FromMilliseconds(700), FillMode = FillMode.Forward, Easing = new CubicEaseOut(),
                Children = { Frame(0, new Setter(StackPanel.SpacingProperty, 4d)), Frame(1, new Setter(StackPanel.SpacingProperty, 0d)) }
            }.RunAsync(_wordmark, cancellationToken: cancellation)
        };
        foreach (var letter in _letters)
            settle.Add(new Animation
            {
                Duration = TimeSpan.FromMilliseconds(700), FillMode = FillMode.Forward, Easing = new CubicEaseOut(),
                Children = { Frame(0, new Setter(MinWidthProperty, 24d)), Frame(1, new Setter(MinWidthProperty, 0d)) }
            }.RunAsync(letter, cancellationToken: cancellation));
        await Task.WhenAll(settle);
        await new Animation
        {
            Duration = TimeSpan.FromMilliseconds(200), FillMode = FillMode.Forward,
            Children = { Frame(0, new Setter(OpacityProperty, 1d)), Frame(1, new Setter(OpacityProperty, 0d)) }
        }.RunAsync(this, cancellationToken: cancellation);
    }
    private static KeyFrame Frame(double cue, params Setter[] setters)
    { var frame = new KeyFrame { Cue = new Cue(cue) }; foreach (var setter in setters) frame.Setters.Add(setter); return frame; }
    private static KeyFrame At(double milliseconds, params Setter[] setters)
    { var frame = new KeyFrame { KeyTime = TimeSpan.FromMilliseconds(milliseconds) }; foreach (var setter in setters) frame.Setters.Add(setter); return frame; }
}
