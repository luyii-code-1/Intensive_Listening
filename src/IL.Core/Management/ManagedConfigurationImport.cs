using System.Text.Json;
using System.Text.Json.Nodes;

namespace IL.Core.Management;

public static class ManagedConfigurationImport
{
    public const int Imported = 0, ManagementLocked = 10;

    public static int Apply(string source, string dataDirectory)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())) throw new PlatformNotSupportedException();
        if (!string.Equals(Path.GetFileName(source), "il_mdn_config.yaml", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("安装配置文件名需要为 il_mdn_config.yaml。");
        var incoming = MdnConfiguration.Load(Path.GetDirectoryName(Path.GetFullPath(source))!)
            ?? throw new FormatException("缺少安装配置。");
        var target = Path.Combine(dataDirectory, "il_mdn_config.yaml");
        var statePath = Path.Combine(dataDirectory, "mdn", "state.bin");
        JsonObject? state = null;
        if (File.Exists(target) && File.Exists(statePath))
        {
            state = JsonNode.Parse(ManagedSecretStore.Load(statePath))!.AsObject();
            var policy = state["policy"]?.Deserialize<ManagedPolicy>(ManagedPolicy.Json)
                ?? throw new FormatException("已有组织策略缓存无效。");
            policy.Validate();
            if (policy.LockManagement) return ManagementLocked;
        }
        var previous = File.Exists(target) ? MdnConfiguration.Load(dataDirectory) : null;
        var sameOrganization = state is not null && previous is not null
            && previous.UpdatePublicKey == incoming.UpdatePublicKey
            && (incoming.UpdatePublicKey.Length > 0 || previous.Server == incoming.Server && previous.EnrollmentToken == incoming.EnrollmentToken);
        Directory.CreateDirectory(dataDirectory);
        File.Copy(source, target + ".tmp", true); File.Move(target + ".tmp", target, true);
        if (sameOrganization)
        {
            // A hostname, IP or protocol change does not invalidate a device credential.
            state!["server"] = incoming.Server.AbsoluteUri;
            ManagedSecretStore.Save(statePath, state.ToJsonString(ManagedPolicy.Json));
        }
        else
        {
            if (File.Exists(statePath)) File.Delete(statePath);
            var cloud = Path.Combine(dataDirectory, "mdn", "cloud.bin"); if (File.Exists(cloud)) File.Delete(cloud);
            var events = Path.Combine(dataDirectory, "mdn", "events.bin");
            if (File.Exists(events)) File.Delete(events);
            var markers = Path.Combine(dataDirectory, "mdn", "crashes");
            if (Directory.Exists(markers)) foreach (var marker in Directory.EnumerateFiles(markers, "*.sent")) File.Delete(marker);
        }
        return Imported;
    }
}
