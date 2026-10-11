using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
namespace IL.Core.Management;

public sealed class MdnConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string OrganizationName { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string EnrollmentToken { get; set; } = "";
    public string UpdatePublicKey { get; set; } = "";
    public int PollSeconds { get; set; } = 60;
    public Uri Server => new(ServerUrl.TrimEnd('/') + "/");
    public static MdnConfiguration? Load(string root)
    {
        var path = Path.Combine(root, "il_mdn_config.yaml");
        if (!File.Exists(path) || !OperatingSystem.IsWindows()) return null;
        if (new FileInfo(path).Length > 32 * 1024) throw new FormatException("集控配置文件过大。");
        var configuration = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
            .Deserialize<MdnConfiguration>(File.ReadAllText(path)) ?? throw new FormatException("集控配置为空。");
        if (configuration.SchemaVersion != 1 || string.IsNullOrWhiteSpace(configuration.OrganizationName) ||
            string.IsNullOrWhiteSpace(configuration.EnrollmentToken) ||
            !Uri.TryCreate(configuration.ServerUrl, UriKind.Absolute, out var server) || server.Scheme != "https" ||
            !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
            throw new FormatException("集控配置需要组织名称、HTTPS 服务器地址和注册凭证。");
        configuration.PollSeconds = Math.Clamp(configuration.PollSeconds, 15, 3600);
        return configuration;
    }
}
