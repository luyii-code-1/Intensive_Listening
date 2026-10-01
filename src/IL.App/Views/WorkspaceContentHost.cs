using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using IL.App.Views.Dialogs;

namespace IL.App.Views;

/// <summary>Seeds incoming pixels before attachment; keeps the outgoing page until the spring settles.</summary>
public sealed class WorkspaceContentHost : Grid
{
    public static readonly StyledProperty<object?> ContentProperty = AvaloniaProperty.Register<WorkspaceContentHost, object?>(nameof(Content));
    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }
    public bool AnimateChanges { get; set; } = true;
    private sealed record Page(ContentPresenter Presenter, SpringMotion Motion);
    private readonly Dictionary<object, Page> _pages = new();
    private Page? _current;
    private long _generation;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty) Present();
    }
    private void Present()
    {
        var previous = _current; var generation = ++_generation;
        foreach (var page in _pages.Values.Where(p => p != previous).ToArray()) { page.Motion.Stop(); Children.Remove(page.Presenter); }
        if (Content is null) { if (previous != null) { previous.Motion.Stop(); Children.Remove(previous.Presenter); } _current = null; _pages.Clear(); return; }
        if (!_pages.TryGetValue(Content, out var incoming))
        {
            var presenter = new ContentPresenter { Content = Content, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
            var motion = new SpringMotion(presenter);
            motion.Set(0, y: FirstRunWizard.MotionReduced() ? 0 : 12);
            _pages[Content] = incoming = new(presenter, motion);
        }
        _current = incoming;
        if (!Children.Contains(incoming.Presenter)) Children.Add(incoming.Presenter);
        incoming.Presenter.IsHitTestVisible = true;
        if (previous == incoming) return;
        if (VisualRoot is null || !AnimateChanges)
        {
            incoming.Motion.Set();
            if (previous != null) { previous.Motion.Stop(); Children.Remove(previous.Presenter); }
            Prune(); return;
        }
        if (previous != null) { previous.Presenter.IsHitTestVisible = false; _ = previous.Motion.To(0, y: -12); }
        _ = CompleteAsync(incoming, generation);
    }
    private async Task CompleteAsync(Page incoming, long generation)
    {
        await incoming.Motion.To();
        if (_generation != generation) return;
        foreach (var old in Children.Where(c => c != incoming.Presenter).ToArray()) Children.Remove(old);
        Prune();
    }
    private void Prune()
    {
        // Cached top-level pages are retained by their owner, not by every rebuilt editor surface.
        foreach (var key in _pages.Where(p => p.Value != _current && !Children.Contains(p.Value.Presenter)).Select(p => p.Key).ToArray())
        { _pages[key].Presenter.Content = null; _pages.Remove(key); }
    }
}
