using System.Text.Json;
using IL.Core.Management;
using Xunit;
namespace IL.Core.Tests;
public class ManagedStateRestoreTests
{
    private static void WithRoot(Action<string> test)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-mdn-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mdn"));
        try { test(root); } finally { Directory.Delete(root, true); }
    }
    private static void State(string root, bool locked)
    {
        File.WriteAllText(Path.Combine(root, "il_mdn_config.yaml"), "existing organization configuration");
        WindowsSecretStore.Save(Path.Combine(root, "mdn", "state.bin"), JsonSerializer.Serialize(new
        { policy = new ManagedPolicy { LockManagement = locked } }, ManagedPolicy.Json));
    }
    [Fact] public void OccupiedCacheRollsBackAndCanBeRetried() => WithRoot(root =>
    {
        State(root, false);
        var state = Path.Combine(root, "mdn", "state.bin"); var original = File.ReadAllBytes(state);
        var cloud = Path.Combine(root, "mdn", "cloud.bin"); File.WriteAllText(cloud, "cloud cache");
        using (var held = new FileStream(cloud, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => ManagedStateRestore.Apply(root));
            Assert.Equal(original, File.ReadAllBytes(state));
            Assert.True(File.Exists(Path.Combine(root, "il_mdn_config.yaml")));
        }
        Assert.Equal(ManagedStateRestore.Cleared, ManagedStateRestore.Apply(root)); Assert.False(File.Exists(state));
    });

    [Fact] public void UnlockedRestorePreservesPersonalDataAndDistributedLessons() => WithRoot(root =>
    {
        State(root, false);
        var preserved = new[] { "settings.json", "device-id.txt", "lesson_progress.json", "library/course.ilp", "projects/project.json", "mdn/lessons/course.ilp" };
        foreach (var name in preserved)
        {
            var p = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, name);
        }
        foreach (var name in new[] { "cloud.bin", "events.bin", "update-receipts.bin" }) File.WriteAllText(Path.Combine(root, "mdn", name), "cached");
        Assert.Equal(ManagedStateRestore.Cleared, ManagedStateRestore.Apply(root));
        Assert.False(File.Exists(Path.Combine(root, "il_mdn_config.yaml")));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "mdn"), "*.bin"));
        foreach (var name in preserved) Assert.Equal(name, File.ReadAllText(Path.Combine(root, name)));
        Assert.Equal(ManagedStateRestore.Cleared, ManagedStateRestore.Apply(root));
    });
    [Fact] public void LockedRestoreLeavesEveryStateFileIntact() => WithRoot(root =>
    {
        State(root, true);
        var state = Path.Combine(root, "mdn", "state.bin"); var original = File.ReadAllBytes(state);
        Assert.Equal(ManagedStateRestore.Locked, ManagedStateRestore.Apply(root));
        Assert.Equal(original, File.ReadAllBytes(state)); Assert.True(File.Exists(Path.Combine(root, "il_mdn_config.yaml")));
    });
    [Fact] public void CorruptAndMissingPolicyCannotAuthorizeRestore() => WithRoot(root =>
    {
        State(root, false);
        var state = Path.Combine(root, "mdn", "state.bin"); File.WriteAllText(state, "unreadable");
        Assert.Equal(ManagedStateRestore.Unreadable, ManagedStateRestore.Apply(root));
        Assert.Equal("unreadable", File.ReadAllText(state));
        File.Delete(state);
        Assert.Equal(ManagedStateRestore.Unreadable, ManagedStateRestore.Apply(root));
        Assert.True(File.Exists(Path.Combine(root, "il_mdn_config.yaml")));
    });
    [Fact] public void UnenrolledDirectoryIsAnIdempotentNoOp() => WithRoot(root =>
    {
        Assert.Equal(ManagedStateRestore.Cleared, ManagedStateRestore.Apply(root));
    });
}
