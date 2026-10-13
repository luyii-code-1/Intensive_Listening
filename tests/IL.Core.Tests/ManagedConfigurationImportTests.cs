using System.Text.Json;
using System.Text.Json.Nodes;
using IL.Core.Management;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IL.Core.Tests;

public sealed class ManagedConfigurationImportTests
{
    private static MdnConfiguration Config(string name, string server, string key, string token) => new()
    { OrganizationName = name, ServerUrl = server, UpdatePublicKey = key, EnrollmentToken = token };
    private static void WriteConfig(string root, MdnConfiguration config)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "il_mdn_config.yaml"), new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build().Serialize(config));
    }
    private static void WriteState(string root, MdnConfiguration config, bool? locked)
    {
        var policy = new JsonObject { ["settings"] = new JsonObject { ["fileAssociationEnabled"] = true } };
        if (locked is not null) policy["lockManagement"] = locked.Value;
        WindowsSecretStore.Save(Path.Combine(root, "mdn", "state.bin"), JsonSerializer.Serialize(new
        { server = config.Server.AbsoluteUri, deviceId = "existing-device", token = "device-credential", policy }));
        WindowsSecretStore.Save(Path.Combine(root, "mdn", "events.bin"), "[]");
        Directory.CreateDirectory(Path.Combine(root, "mdn", "crashes"));
        File.WriteAllText(Path.Combine(root, "mdn", "crashes", "old.sent"), "sent");
        File.WriteAllText(Path.Combine(root, "device-id.txt"), "existing-device");
    }
    private static void WithRoot(Action<string> test)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-mdn-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); } finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void UnlockedAndLegacyOrganizationsAreReplaced(bool? locked) => WithRoot(root =>
    {
        var data = Path.Combine(root, "data"); var incoming = Path.Combine(root, "installer");
        var previous = Config("Previous", "https://previous.example/", "previous-key", "previous-token");
        WriteConfig(data, previous); WriteState(data, previous, locked);
        WriteConfig(incoming, Config("New", "https://new.example/", "new-key", "new-token"));
        Assert.Equal(ManagedConfigurationImport.Imported, ManagedConfigurationImport.Apply(Path.Combine(incoming, "il_mdn_config.yaml"), data));
        Assert.Equal("New", MdnConfiguration.Load(data)!.OrganizationName);
        Assert.False(File.Exists(Path.Combine(data, "mdn", "state.bin")));
        Assert.False(File.Exists(Path.Combine(data, "mdn", "events.bin")));
        Assert.False(File.Exists(Path.Combine(data, "mdn", "crashes", "old.sent")));
        Assert.Equal("existing-device", File.ReadAllText(Path.Combine(data, "device-id.txt")));
    });

    [Fact]
    public void NewInstallationImportsWithoutExistingCache() => WithRoot(root =>
    {
        var data = Path.Combine(root, "new data"); var incoming = Path.Combine(root, "installer");
        WriteConfig(incoming, Config("New", "https://new.example/", "new-key", "new-token"));
        Assert.Equal(ManagedConfigurationImport.Imported, ManagedConfigurationImport.Apply(Path.Combine(incoming, "il_mdn_config.yaml"), data));
        Assert.Equal("New", MdnConfiguration.Load(data)!.OrganizationName);
    });

    [Fact]
    public void LockedOrganizationKeepsItsConfigurationAndCredential() => WithRoot(root =>
    {
        var data = Path.Combine(root, "data"); var incoming = Path.Combine(root, "installer");
        var previous = Config("Previous", "https://previous.example/", "previous-key", "previous-token");
        WriteConfig(data, previous); WriteState(data, previous, true);
        var cached = File.ReadAllBytes(Path.Combine(data, "mdn", "state.bin"));
        WriteConfig(incoming, Config("New", "https://new.example/", "new-key", "new-token"));
        Assert.Equal(ManagedConfigurationImport.ManagementLocked, ManagedConfigurationImport.Apply(Path.Combine(incoming, "il_mdn_config.yaml"), data));
        Assert.Equal("Previous", MdnConfiguration.Load(data)!.OrganizationName);
        Assert.Equal(cached, File.ReadAllBytes(Path.Combine(data, "mdn", "state.bin")));
    });

    [Fact]
    public void SameOrganizationAddressChangePreservesCredentialAndPolicy() => WithRoot(root =>
    {
        var data = Path.Combine(root, "data"); var incoming = Path.Combine(root, "installer");
        var previous = Config("Organization", "https://old.example/", "same-key", "same-token");
        WriteConfig(data, previous); WriteState(data, previous, false);
        WriteConfig(incoming, Config("Organization", "https://new.example/", "same-key", "rotated-enrollment-token"));
        ManagedConfigurationImport.Apply(Path.Combine(incoming, "il_mdn_config.yaml"), data);
        var state = JsonNode.Parse(WindowsSecretStore.Load(Path.Combine(data, "mdn", "state.bin")))!;
        Assert.Equal("https://new.example/", state["server"]!.GetValue<string>());
        Assert.Equal("device-credential", state["token"]!.GetValue<string>());
        Assert.True(state["policy"]!["settings"]!["fileAssociationEnabled"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(data, "mdn", "events.bin")));
    });

    [Fact]
    public void InvalidIncomingConfigurationKeepsExistingOrganization() => WithRoot(root =>
    {
        var data = Path.Combine(root, "data"); var incoming = Path.Combine(root, "installer");
        var previous = Config("Previous", "https://previous.example/", "key", "token");
        WriteConfig(data, previous); WriteState(data, previous, false);
        WriteConfig(incoming, Config("Invalid", "http://new.example/", "new-key", "new-token"));
        Assert.Throws<FormatException>(() => ManagedConfigurationImport.Apply(Path.Combine(incoming, "il_mdn_config.yaml"), data));
        Assert.Equal("Previous", MdnConfiguration.Load(data)!.OrganizationName);
    });

    [Fact]
    public void MissingPollingSettingUsesOneMinute() => WithRoot(root =>
    {
        File.WriteAllText(Path.Combine(root, "il_mdn_config.yaml"), "schema_version: 1\norganization_name: Test\nserver_url: https://example.org/\nenrollment_token: test\n");
        Assert.Equal(60, MdnConfiguration.Load(root)!.PollSeconds);
    });
}
