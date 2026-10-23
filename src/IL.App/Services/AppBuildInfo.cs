using System.Reflection;

namespace IL.App.Services;

public static class AppBuildInfo
{
    public const string ReleaseName = "2 Resonance";
    private static readonly string Identity = typeof(AppBuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "2.2.0-insider.20261023.1";
    public static string UpdateVersion => Identity.Split('+')[0];
    public static string Version => UpdateVersion.Split('-')[0];
    public static string DisplayVersion => FormatVersion(UpdateVersion);
    public static string? SuccessfulUpdateVersion { get; set; }
    public static string Commit => Identity.Contains('+') ? Identity.Split('+')[1][..Math.Min(7, Identity.Split('+')[1].Length)] : "local";
    public static string Display => $"{ReleaseName} · {DisplayVersion} · 构建 {Commit}";
    internal static string FormatVersion(string identity)
    {
        var parts = identity.Split('+')[0].Split('-', 2);
        var version = "v" + parts[0];
        if (parts.Length == 1) return version;
        var suffix = parts[1].Split('.');
        if (suffix.Length >= 2 && DateTime.TryParseExact(suffix[1], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
        {
            if (suffix[0] == "beta") return $"{version} Beta {date:MMdd}";
            if (suffix[0] == "insider" && suffix.Length == 3 && int.TryParse(suffix[2], out var sequence)) return $"{version} Insider Preview {date:MMdd} {sequence}";
        }
        return "v" + identity.Split('+')[0];
    }
}
