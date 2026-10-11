using IL.Core.Management;
using Xunit;

namespace IL.Core.Tests;

public sealed class MdnEnrollmentTests
{
    [Fact]
    public void ManualEnrollmentYamlSurvivesRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-mdn-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new MdnConfiguration { OrganizationName = "测试学校", ServerUrl = "https://192.168.1.10:8443/", EnrollmentToken = "test-token", ServerCertificateSha256 = new string('a', 64) };
            var yaml = new YamlDotNet.Serialization.SerializerBuilder().WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance).Build().Serialize(config);
            File.WriteAllText(Path.Combine(root, "il_mdn_config.yaml"), yaml);
            var loaded = MdnConfiguration.Load(root)!;
            Assert.Equal(config.Server, loaded.Server); Assert.Equal(config.ServerCertificateSha256, loaded.ServerCertificateSha256);
            Assert.Equal(config.EnrollmentToken, loaded.EnrollmentToken);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("192.168.1.10:8443", "https://192.168.1.10:8443/")]
    [InlineData(" https://192.168.1.10:8443/ ", "https://192.168.1.10:8443/")]
    [InlineData("[::1]:8443", "https://[::1]:8443/")]
    public void IpAddressesAreNormalized(string input, string expected) => Assert.Equal(expected, MdnEnrollment.NormalizeAddress(input).AbsoluteUri);

    [Theory]
    [InlineData("http://192.168.1.10:8443")]
    [InlineData("https://user:password@example.org")]
    [InlineData("https://example.org/path")]
    [InlineData("https://example.org?token=secret")]
    [InlineData("https://example.org/#fragment")]
    public void EnrollmentRejectsInsecureOrAmbiguousAddresses(string input) => Assert.Throws<FormatException>(() => MdnEnrollment.NormalizeAddress(input));
}
