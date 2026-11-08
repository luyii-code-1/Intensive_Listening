using IL.Core.Infrastructure;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IL.Core.Management;

public static class ManagedStateRestore
{
    public const int Cleared = 0, Locked = 10, Unreadable = 11, ApplicationRunning = 12;
    public static int Apply(string dataDirectory)
    {
        var root = Path.GetFullPath(dataDirectory);
        RecoverableFileBatch.Recover(root, ".mdn-restore");
        var mdn = Path.Combine(root, "mdn");
        var configuration = Path.Combine(root, "il_mdn_config.yaml");
        var state = Path.Combine(mdn, "state.bin");
        // An unreadable or missing policy cannot authorize removing an enrolled organization.
        if (File.Exists(state))
        {
            try
            {
                var json = JsonNode.Parse(ManagedSecretStore.Load(state))?.AsObject();
                var policy = json?["policy"]?.Deserialize<ManagedPolicy>(ManagedPolicy.Json)
                    ?? throw new FormatException("缺少组织策略缓存。");
                policy.Validate();
                if (policy.LockManagement) return Locked;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { return Unreadable; }
        }
        else if (File.Exists(configuration)) return Unreadable;

        var changes = new List<(string Path, byte[]? Content)>();
        foreach (var name in new[] { "state.bin", "cloud.bin", "events.bin", "update-receipts.bin" })
            if (File.Exists(Path.Combine(mdn, name))) changes.Add((Path.Combine("mdn", name), null));
        var markers = Path.Combine(mdn, "crashes");
        if (Directory.Exists(markers))
            foreach (var marker in Directory.EnumerateFiles(markers, "*.sent")) changes.Add((Path.GetRelativePath(root, marker), null));
        if (File.Exists(configuration)) changes.Add(("il_mdn_config.yaml", null));
        if (changes.Count > 0) RecoverableFileBatch.Apply(root, ".mdn-restore", changes);
        return Cleared;
    }
}
