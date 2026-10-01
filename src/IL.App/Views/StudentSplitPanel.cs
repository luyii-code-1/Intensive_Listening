using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IL.App.Views;

internal sealed class StudentSplitPanel : Panel
{
    public StudentSplitPanel(Control player, Control transcript)
    {
        Children.Add(player);
        Children.Add(new Border { Background = Brush.Parse("#16000000") });
        Children.Add(transcript);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        if (availableSize.Width < 900)
        {
            var playerHeight = Math.Min(420, availableSize.Height);
            Children[0].Measure(new Size(availableSize.Width, playerHeight));
            Children[1].Measure(new Size(availableSize.Width, 1));
            Children[2].Measure(new Size(availableSize.Width, Math.Max(0, availableSize.Height - playerHeight - 1)));
        }
        else
        {
            var playerWidth = (availableSize.Width - 1) * 100 / 268;
            Children[0].Measure(new Size(playerWidth, availableSize.Height));
            Children[1].Measure(new Size(1, availableSize.Height));
            Children[2].Measure(new Size(availableSize.Width - playerWidth - 1, availableSize.Height));
        }
        return availableSize;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width < 900)
        {
            var playerHeight = Math.Min(420, finalSize.Height);
            Children[0].Arrange(new Rect(0, 0, finalSize.Width, playerHeight));
            Children[1].Arrange(new Rect(0, playerHeight, finalSize.Width, 1));
            Children[2].Arrange(new Rect(0, playerHeight + 1, finalSize.Width, Math.Max(0, finalSize.Height - playerHeight - 1)));
        }
        else
        {
            var playerWidth = (finalSize.Width - 1) * 100 / 268;
            Children[0].Arrange(new Rect(0, 0, playerWidth, finalSize.Height));
            Children[1].Arrange(new Rect(playerWidth, 0, 1, finalSize.Height));
            Children[2].Arrange(new Rect(playerWidth + 1, 0, finalSize.Width - playerWidth - 1, finalSize.Height));
        }
        return finalSize;
    }
}

internal sealed class StudentCenteredWrapPanel : Panel
{
    private const double Gap = 8;
    public StudentCenteredWrapPanel(params Control[] controls)
    {
        foreach (var control in controls) Children.Add(control);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        double rowWidth = 0, rowHeight = 0, height = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var size = child.DesiredSize;
            if (rowWidth > 0 && rowWidth + Gap + size.Width > availableSize.Width)
            { width = Math.Max(width, rowWidth); height += rowHeight + Gap; rowWidth = rowHeight = 0; }
            rowWidth += (rowWidth > 0 ? Gap : 0) + size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        width = Math.Max(width, rowWidth); height += rowHeight;
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : width, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = new List<List<Control>>();
        double width = 0;
        foreach (var child in Children)
        {
            if (rows.Count == 0 || width > 0 && width + Gap + child.DesiredSize.Width > finalSize.Width)
            { rows.Add([]); width = 0; }
            rows[^1].Add(child); width += (width > 0 ? Gap : 0) + child.DesiredSize.Width;
        }
        double y = 0;
        foreach (var row in rows)
        {
            var rowHeight = row.Max(child => child.DesiredSize.Height);
            var x = (finalSize.Width - row.Sum(child => child.DesiredSize.Width) - Gap * (row.Count - 1)) / 2;
            foreach (var child in row)
            { child.Arrange(new Rect(x, y + (rowHeight - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height)); x += child.DesiredSize.Width + Gap; }
            y += rowHeight + Gap;
        }
        return finalSize;
    }
}
