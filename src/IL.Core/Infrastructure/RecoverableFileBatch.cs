using System.Text.Json;

namespace IL.Core.Infrastructure;

/// <summary>A journaled batch of replacements/removals within one application data directory.</summary>
internal static class RecoverableFileBatch
{
    private sealed record Entry(string Path, bool HadOriginal, bool Replacement);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static void Recover(string root, string name)
    {
        var work = Path.Combine(root, name);
        if (!Directory.Exists(work)) return;
        var journal = Path.Combine(work, "journal.json");
        if (File.Exists(journal) && !File.Exists(Path.Combine(work, "committed")))
        {
            var entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(journal), Json)
                ?? throw new IOException("文件事务日志无效，已保留恢复文件。");
            for (var i = entries.Length - 1; i >= 0; i--)
            {
                var entry = entries[i]; var target = Target(root, entry.Path);
                var backup = Path.Combine(work, i + ".old");
                if (File.Exists(backup)) File.Move(backup, target, true);
                else if (!entry.HadOriginal && entry.Replacement && !File.Exists(Path.Combine(work, i + ".new"))) File.Delete(target);
            }
        }
        Directory.Delete(work, true);
    }
    public static void Apply(string root, string name, IReadOnlyList<(string Path, byte[]? Content)> changes, CancellationToken ct = default, IReadOnlyDictionary<string, string>? sources = null)
    {
        Recover(root, name);
        Directory.CreateDirectory(root);
        var work = Path.Combine(root, name); Directory.CreateDirectory(work);
        try
        {
            var entries = changes.Select(change => new Entry(change.Path, File.Exists(Target(root, change.Path)), (change.Content is not null || sources?.ContainsKey(change.Path) == true))).ToArray();
            for (var i = 0; i < changes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var staged = Path.Combine(work, i + ".new");
                if (changes[i].Content is { } content) File.WriteAllBytes(staged, content);
                else if (sources is not null && sources.TryGetValue(changes[i].Path, out var source))
                {
                    using var input = File.OpenRead(source);
                    using var output = File.Create(staged);
                    var buffer = new byte[65536]; int count;
                    while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                }
            }
            var journal = Path.Combine(work, "journal.json");
            using (var file = new FileStream(journal + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(entries, Json)); file.Flush(true);
            }
            File.Move(journal + ".tmp", journal);
            for (var i = 0; i < entries.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var target = Target(root, entries[i].Path);
                if (entries[i].HadOriginal) File.Move(target, Path.Combine(work, i + ".old"));
                if (entries[i].Replacement) File.Move(Path.Combine(work, i + ".new"), target);
            }
            using (var committed = new FileStream(Path.Combine(work, "committed"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) committed.Flush(true);
        }
        catch (Exception original)
        {
            try { Recover(root, name); }
            catch (Exception recovery) { throw new AggregateException("操作失败，恢复文件已保留；请解除文件占用后重试。", original, recovery); }
            throw;
        }
        // The commit marker makes cleanup retryable without reverting a successful save.
        try { Recover(root, name); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppLog.Warning("已提交文件事务的备份暂未清理", error); }
    }
    private static string Target(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !target.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("文件事务路径无效。");
        return target;
    }
}
