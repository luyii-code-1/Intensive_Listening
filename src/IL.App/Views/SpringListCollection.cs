using Avalonia.Controls;
using Avalonia.VisualTree;
using IL.App.Views.Dialogs;

namespace IL.App.Views;

/// <summary>Keyed rows keep their identity while insertions, removals and layout moves settle.</summary>
internal sealed class SpringListCollection<T>(Control owner, IList<T> items, Func<Border, T> create, Action<int, int> move) where T : Control
{
    private sealed class Row(T container, Border frame)
    {
        public readonly T Container = container;
        public readonly Border Frame = frame;
        public readonly SpringMotion Motion = SpringMotion.For(frame);
        public bool Wanted = true, Entering = true;
        public int ExitVersion;
    }
    private readonly Dictionary<string, Row> _rows = new();
    private readonly Dictionary<Row, double> _positions = new();
    private EventHandler? _arranged;
    public void Update(IEnumerable<(string Key, Control Content)> source)
    {
        CapturePositions();
        var entries = source.ToArray(); var keys = entries.Select(e => e.Key).ToHashSet();
        var outgoing = items.Select((item, index) => (item, index)).Where(p => !keys.Contains(p.item.Tag as string ?? "")).ToArray();
        var desired = new List<T>();
        foreach (var (key, content) in entries)
        {
            if (!_rows.TryGetValue(key, out var row))
            {
                var frame = new Border { Child = content }; var container = create(frame); container.Tag = key;
                _rows[key] = row = new(container, frame);
                if (owner.IsAttachedToVisualTree()) row.Motion.Set(0, y: FirstRunWizard.MotionReduced() ? 0 : 8, scale: FirstRunWizard.MotionReduced() ? 1 : .985);
            }
            else
            {
                if (!row.Wanted) { row.Wanted = true; row.ExitVersion++; _ = row.Motion.To(response: .28); }
                if (!ReferenceEquals(row.Frame.Child, content)) row.Frame.Child = content;
            }
            row.Container.IsHitTestVisible = true; desired.Add(row.Container);
        }
        foreach (var (item, index) in outgoing) desired.Insert(Math.Min(index, desired.Count), item);
        for (var i = 0; i < desired.Count; i++)
        {
            var current = items.IndexOf(desired[i]);
            if (current < 0) items.Insert(i, desired[i]);
            else if (current != i) move(current, i);
        }
        foreach (var row in _rows.Values.Where(r => !keys.Contains(r.Container.Tag as string ?? "") && r.Wanted).ToArray())
        {
            row.Wanted = false; row.Container.IsHitTestVisible = false;
            _ = RemoveAsync(row);
        }
        ArrangeMotion();
    }
    private void CapturePositions()
    {
        foreach (var row in _rows.Values)
            if (!_positions.ContainsKey(row) && row.Container.IsAttachedToVisualTree() && row.Container.Bounds.Height > 0)
                _positions[row] = row.Container.Bounds.Y + row.Motion.TranslationY;
    }
    private void ArrangeMotion()
    {
        if (!owner.IsAttachedToVisualTree())
        {
            foreach (var row in _rows.Values) { row.Motion.Set(); row.Entering = false; }
            _positions.Clear(); return;
        }
        if (_arranged != null) return;
        _arranged = (_, _) =>
        {
            owner.LayoutUpdated -= _arranged; _arranged = null;
            foreach (var row in _rows.Values)
            {
                if (!row.Container.IsAttachedToVisualTree() || row.Container.Bounds.Height <= 0) { row.Entering = false; continue; }
                if (row.Entering && row.Wanted)
                {
                    row.Motion.Set(0, y: FirstRunWizard.MotionReduced() ? 0 : 8, scale: FirstRunWizard.MotionReduced() ? 1 : .985);
                    _ = row.Motion.To(response: .28);
                }
                else if (_positions.TryGetValue(row, out var before) && !FirstRunWizard.MotionReduced())
                {
                    var delta = before - row.Container.Bounds.Y - row.Motion.TranslationY;
                    if (Math.Abs(delta) > .1)
                    {
                        var version = ++row.ExitVersion;
                        var settling = row.Motion.ShiftLayout(delta);
                        if (!row.Wanted) _ = RemoveAfterAsync(row, settling, version);
                    }
                }
                row.Entering = false;
            }
            _positions.Clear();
        };
        owner.LayoutUpdated += _arranged;
        owner.InvalidateMeasure();
    }
    private Task RemoveAsync(Row row)
    {
        var version = ++row.ExitVersion;
        return RemoveAfterAsync(row, row.Motion.To(0, y: -8, scale: .985, response: .24), version);
    }
    private async Task RemoveAfterAsync(Row row, Task settling, int version)
    {
        await settling;
        if (row.Wanted || version != row.ExitVersion) return;
        CapturePositions();
        items.Remove(row.Container); row.Frame.Child = null; _rows.Remove((string)row.Container.Tag!); _positions.Remove(row);
        ArrangeMotion();
    }
}
