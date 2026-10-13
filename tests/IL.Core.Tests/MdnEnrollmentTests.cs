using IL.Core.Management;
using Xunit;

namespace IL.Core.Tests;

public sealed class MdnEnrollmentTests
{
    [Fact]
    public async Task SelectedYamlSupportsCustomFilenameAndRejectsManagedClient()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-mdn-selected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "school.yml");
            await File.WriteAllTextAsync(path, "schema_version: 1\norganization_name: Test\nserver_url: http://127.0.0.1:8443/\nenrollment_token: test-token\ndisable_ssl: true\npoll_seconds: 120\n");
            var selected = MdnConfiguration.LoadFile(path);
            Assert.True(selected.DisableSsl); Assert.Equal(120, selected.PollSeconds);
            File.Copy(path, Path.Combine(root, "il_mdn_config.yaml"));
            await using var client = new MdnClient(root, "2.0.1");
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.JoinYamlAsync(path));
            Assert.Equal("Test", client.Configuration!.OrganizationName);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualEnrollmentYamlSurvivesRestart(bool disableSsl)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-mdn-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new MdnConfiguration { OrganizationName = "测试学校", ServerUrl = (disableSsl ? "http" : "https") + "://192.168.1.10:8443/", EnrollmentToken = "test-token", ServerCertificateSha256 = disableSsl ? "" : new string('a', 64), DisableSsl = disableSsl };
            var yaml = new YamlDotNet.Serialization.SerializerBuilder().WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance).Build().Serialize(config);
            File.WriteAllText(Path.Combine(root, "il_mdn_config.yaml"), yaml);
            var loaded = MdnConfiguration.Load(root)!;
            Assert.Equal(config.Server, loaded.Server); Assert.Equal(config.ServerCertificateSha256, loaded.ServerCertificateSha256);
            Assert.Equal(config.EnrollmentToken, loaded.EnrollmentToken);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedTransportCannotBeChangedWhileDisconnectedOrAfterRestart(bool disableSsl)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-mdn-locked-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "il_mdn_config.yaml");
            await File.WriteAllTextAsync(path, $"schema_version: 1\norganization_name: Test\nserver_url: {(disableSsl ? "http" : "https")}://127.0.0.1:8443/\nenrollment_token: test-token\ndisable_ssl: {disableSsl.ToString().ToLowerInvariant()}\n");
            var before = await File.ReadAllBytesAsync(path);
            for (var restart = 0; restart < 2; restart++)
            {
                await using var client = new MdnClient(root, "2.0.1");
                var original = client.Configuration;
                var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChangeTransportAsync(!disableSsl, new string('a', 64)));
                Assert.Contains("已锁定", denied.Message);
                Assert.Same(original, client.Configuration); Assert.Equal(disableSsl, client.Configuration!.DisableSsl);
                Assert.Equal(before, await File.ReadAllBytesAsync(path));
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.JoinYamlAsync(path));
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("192.168.1.10:8443", "https://192.168.1.10:8443/")]
    [InlineData(" https://192.168.1.10:8443/ ", "https://192.168.1.10:8443/")]
    [InlineData("[::1]:8443", "https://[::1]:8443/")]
    public void IpAddressesAreNormalized(string input, string expected) => Assert.Equal(expected, MdnEnrollment.NormalizeAddress(input).AbsoluteUri);

    [Fact]
    public void HttpRequiresExplicitTransportChoice()
    {
        Assert.Equal("http://192.168.1.10:8443/", MdnEnrollment.NormalizeAddress("192.168.1.10:8443", true).AbsoluteUri);
        Assert.Equal("http", MdnEnrollment.NormalizeAddress("http://192.168.1.10:8443", true).Scheme);
        Assert.Throws<FormatException>(() => MdnEnrollment.NormalizeAddress("https://192.168.1.10:8443", true));
    }

    [Theory]
    [InlineData("http://192.168.1.10:8443")]
    [InlineData("https://user:password@example.org")]
    [InlineData("https://example.org/path")]
    [InlineData("https://example.org?token=secret")]
    [InlineData("https://example.org/#fragment")]
    public void EnrollmentRejectsInsecureOrAmbiguousAddresses(string input) => Assert.Throws<FormatException>(() => MdnEnrollment.NormalizeAddress(input));
}
