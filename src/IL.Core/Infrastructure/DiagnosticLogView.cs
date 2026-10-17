namespace IL.Core.Infrastructure;

public sealed record DiagnosticViewFrame(DiagnosticEntry[] Entries, long Latest, long Oldest, int Buffered, int Matching, int Skipped);

public static class DiagnosticLogView
{
    public const int Capacity = 1000;
    public const int BatchSize = 200;
    public static DiagnosticViewFrame Read(IReadOnlyList<DiagnosticEntry> snapshot, string query, string? level, string? source, long cleared, long after, bool reset)
    {
        var matching = snapshot.Where(entry => entry.Sequence > cleared && (level == null || entry.Level == level)
            && (source == null || (source == "操作与应用" ? entry.Category is not ("runtime" or "network" or "first-chance" or "trace") : entry.Category == source))
            && (query.Length == 0 || entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.ThreadId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        var pending = reset ? matching : matching.Where(entry => entry.Sequence > after).ToArray();
        var limit = reset ? Capacity : BatchSize;
        return new(pending.TakeLast(limit).ToArray(), snapshot.LastOrDefault()?.Sequence ?? 0,
            snapshot.FirstOrDefault()?.Sequence ?? 0, snapshot.Count, matching.Length, reset ? 0 : Math.Max(0, pending.Length - limit));
    }
}
