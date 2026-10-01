using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace IL.App.Views;

internal sealed class PlaybackGlyph : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<PlaybackGlyph>();
    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<PlaybackGlyph, bool>(nameof(IsPlaying));
    private static readonly Geometry Play = Geometry.Parse("M 5.3,3 Q 4,2.3 4,3.8 L 4,18.2 Q 4,19.7 5.3,19 L 18.7,12.1 Q 20.3,11 18.7,9.9 Z");
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    static PlaybackGlyph() => AffectsRender<PlaybackGlyph>(ForegroundProperty, IsPlayingProperty);
    public PlaybackGlyph() { Width = Height = 22; IsHitTestVisible = false; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (IsPlaying)
        {
            context.DrawRectangle(Foreground, null, new Rect(4, 3, 4.5, 16), 1.15, 1.15);
            context.DrawRectangle(Foreground, null, new Rect(13.5, 3, 4.5, 16), 1.15, 1.15);
        }
        else context.DrawGeometry(Foreground, null, Play);
    }
}
