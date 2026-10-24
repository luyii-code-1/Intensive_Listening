using Avalonia;
using Avalonia.Controls;

namespace IL.App.Views;

/// <summary>Places an action beside its content when both fit, otherwise below it.</summary>
internal sealed class ResponsivePair : Panel
{
    public double MinimumContentWidth { get; init; } = 220;
    public double Gap { get; init; } = 12;
    private bool _stacked;
    public ResponsivePair(Control content, Control action) { Children.Add(content); Children.Add(action); }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        Children[1].Measure(new Size(width, double.PositiveInfinity));
        var action = Children[1].DesiredSize;
        _stacked = double.IsFinite(width) && width < MinimumContentWidth + Gap + action.Width;
        Children[0].Measure(new Size(_stacked ? width : Math.Max(0, width - action.Width - Gap), double.PositiveInfinity));
        var content = Children[0].DesiredSize;
        return _stacked
            ? new Size(Math.Max(content.Width, action.Width), content.Height + Gap + action.Height)
            : new Size(content.Width + Gap + action.Width, Math.Max(content.Height, action.Height));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var action = Children[1].DesiredSize;
        if (_stacked)
        {
            var height = Children[0].DesiredSize.Height;
            Children[0].Arrange(new Rect(0, 0, finalSize.Width, height));
            Children[1].Arrange(new Rect(0, height + Gap, finalSize.Width, action.Height));
        }
        else
        {
            var width = Math.Min(action.Width, finalSize.Width);
            Children[0].Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - width - Gap), finalSize.Height));
            Children[1].Arrange(new Rect(finalSize.Width - width, 0, width, finalSize.Height));
        }
        return finalSize;
    }
}
