namespace IL.Core.Updates;

public sealed class ManagedUpdateJournal(string root)
{
    public static bool AllowsVersion(string current, string offered) =>
        ManagedVersion.TryParse(current, out var installed) && ManagedVersion.TryParse(offered, out var next) && next.CompareTo(installed) >= 0;
    public static bool IsSameVersion(string current, string? offered) => ManagedVersion.TryParse(current, out var installed) && ManagedVersion.TryParse(offered, out var next) && installed.CompareTo(next) == 0;
    private string PathFor(string id, string suffix) => Path.Combine(root, Guid.Parse(id).ToString("D") + suffix);
    public bool Completed(string id) => File.Exists(PathFor(id, ".completed"));
    public bool Attempted(string id) => File.Exists(PathFor(id, ".attempt"));
    public bool NoticeOnce(string id) => Once(id, ".notified");
    public bool AttemptOnce(string id) => Once(id, ".attempt");
    private bool Once(string id, string suffix)
    {
        Directory.CreateDirectory(root);
        try { using var file = new FileStream(PathFor(id, suffix), FileMode.CreateNew, FileAccess.Write, FileShare.None); return true; }
        catch (IOException) when (File.Exists(PathFor(id, suffix))) { return false; }
    }
    public void MarkCompleted(string id, string version)
    {
        Directory.CreateDirectory(root); File.WriteAllText(PathFor(id, ".completed"), version);
    }
}
