using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace IL.App.Views;

internal sealed class RoundedArrowGlyph : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<RoundedArrowGlyph>();
    private static readonly Geometry Right = Geometry.Parse("M 6,3 L 12,9 L 6,15");
    private static readonly Geometry Left = Geometry.Parse("M 12,3 L 6,9 L 12,15");
    private readonly bool _back;
    static RoundedArrowGlyph() => AffectsRender<RoundedArrowGlyph>(ForegroundProperty);
    public RoundedArrowGlyph(bool back = false) { _back = back; Width = Height = 18; IsHitTestVisible = false; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var pen = new Pen(GetValue(ForegroundProperty), 2.2) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        context.DrawGeometry(null, pen, _back ? Left : Right);
    }
}
