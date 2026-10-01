using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IL.App.Views.Dialogs;
using IL.Core.Infrastructure;

namespace IL.App.Views;

/// <summary>Popup hosts keep their normal dismissal and focus behavior; a noninteractive exit surface finishes the spring.</summary>
internal sealed class PopupMotion
{
    private static readonly ConditionalWeakTable<Popup, PopupMotion> States = new();
    private static bool _installed;
    private readonly Popup _popup;
    private Control? _surface;
    private SpringMotion? _motion;
    private TopLevel? _owner;
    private PixelPoint _position;
    private RenderTargetBitmap? _exitBitmap;
    private Size _exitSize;
    private double _capturedOpacity = 1, _capturedScale = 1, _capturedY;
    private Border? _exit;
    private bool _open;
    public static void Install()
    {
        if (_installed) return; _installed = true;
        Popup.ChildProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            if (change.NewValue is Control child) States.GetValue(popup, p => new(p)).Prepare(child);
        });
    }
    private PopupMotion(Popup popup)
    {
        _popup = popup;
        popup.Opened += (_, _) => Open();
        popup.Closed += (_, _) => Close();
    }
    private void Prepare(Control child)
    {
        _popup.ShouldUseOverlayLayer = true;
        if (_surface != null) _surface.DetachedFromVisualTree -= CaptureExit;
        _surface = child; child.DetachedFromVisualTree += CaptureExit;
        _motion = SpringMotion.For(child);
        Seed();
    }
    private void Seed() => _motion?.Set(0, y: FirstRunWizard.MotionReduced() ? 0 : 6, scale: FirstRunWizard.MotionReduced() ? 1 : .96);
    private void Open()
    {
        var reopening = _exit;
        var opacity = (reopening?.Opacity ?? 0) * _capturedOpacity;
        var exitTransform = reopening?.RenderTransform as TransformGroup;
        var scale = (exitTransform?.Children.OfType<ScaleTransform>().FirstOrDefault()?.ScaleX ?? 1) * _capturedScale;
        var y = (exitTransform?.Children.OfType<TranslateTransform>().FirstOrDefault()?.Y ?? 0) + _capturedY;
        ClearExit(); _exitBitmap?.Dispose(); _exitBitmap = null;
        if (reopening != null) _motion?.Set(opacity, y: y, scale: scale);
        _open = true; _owner = _popup.PlacementTarget is { } target ? target.GetLogicalAncestors().OfType<Window>().FirstOrDefault() ?? TopLevel.GetTopLevel(target) : null;
        if (_surface == null) return;
        _surface.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        RememberPosition();
        // The host has already arranged the surface when Opened fires.
        _ = _motion!.To(response: .26);
        Dispatcher.UIThread.Post(() => { if (_open) RememberPosition(); }, DispatcherPriority.Loaded);
    }
    private void RememberPosition()
    {
        if (_surface?.IsAttachedToVisualTree() != true) return;
        // Ignore the animated transform when recording the settled popup location.
        var transform = _surface.RenderTransform; _surface.RenderTransform = null;
        try { _position = _surface.PointToScreen(default); }
        finally { _surface.RenderTransform = transform; }
    }
    private void CaptureExit(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (!_open || _surface == null || _owner?.IsVisible != true || _surface.Bounds.Width <= 0 || _surface.Bounds.Height <= 0) return;
        _exitSize = _surface.Bounds.Size;
        _capturedOpacity = _surface.Opacity;
        _capturedY = _motion?.TranslationY ?? 0;
        _capturedScale = (_surface.RenderTransform as TransformGroup)?.Children.OfType<ScaleTransform>().FirstOrDefault()?.ScaleX ?? 1;
        var scaling = _owner.RenderScaling;
        var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_exitSize.Width * scaling), (int)Math.Ceiling(_exitSize.Height * scaling)), new Vector(96 * scaling, 96 * scaling));
        try { bitmap.Render(_surface); _exitBitmap?.Dispose(); _exitBitmap = bitmap; }
        catch (Exception ex) { bitmap.Dispose(); AppLog.Error("浮窗动画绘制失败", ex); }
    }
    private void Close()
    {
        _open = false;
        if (_exitBitmap is { } bitmap && _owner?.IsVisible == true && OverlayLayer.GetOverlayLayer(_owner) is { } overlay)
        {
            var point = _owner.PointToClient(_position); var offset = _owner.TranslatePoint(point, overlay) ?? point;
            var image = new Image { Source = bitmap, Stretch = Stretch.Fill, Width = _exitSize.Width, Height = _exitSize.Height };
            var exit = new Border { Name = "PopupExitSurface", Child = image, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(offset.X, offset.Y, 0, 0), RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative) };
            _exit = exit; _exitBitmap = null; overlay.Children.Add(exit);
            var motion = SpringMotion.For(exit); exit.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            _ = FinishExitAsync(exit, motion, bitmap);
        }
        else { _exitBitmap?.Dispose(); _exitBitmap = null; }
        Seed();
    }
    private async Task FinishExitAsync(Border exit, SpringMotion motion, RenderTargetBitmap bitmap)
    {
        try { await motion.To(0, y: 6, scale: .96, response: .20); }
        finally
        {
            if (exit.Parent is Panel panel) panel.Children.Remove(exit);
            exit.Child = null; bitmap.Dispose();
            if (ReferenceEquals(_exit, exit)) _exit = null;
        }
    }
    private void ClearExit()
    {
        if (_exit == null) return;
        SpringMotion.For(_exit).Stop();
        if (_exit.Parent is Panel panel) panel.Children.Remove(_exit);
        _exit = null;
    }
}
