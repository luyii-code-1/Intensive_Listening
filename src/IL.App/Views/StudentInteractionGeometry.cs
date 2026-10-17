using Avalonia;

namespace IL.App.Views;

internal static class StudentInteractionGeometry
{
    public static double? ClozeHitDistance(Size size, Point position, bool hidden)
    {
        if (size.Width <= 0 || size.Height <= 0) return null;
        var original = new Rect(size);
        if (original.Contains(position)) return 0;
        var hit = new Rect(-size.Width * .125, -size.Height * .125, size.Width * 1.25, size.Height * 1.25);
        if (!hidden || !hit.Contains(position)) return null;
        var dx = Math.Max(0, Math.Max(-position.X, position.X - size.Width));
        var dy = Math.Max(0, Math.Max(-position.Y, position.Y - size.Height));
        return dx * dx + dy * dy;
    }
}
