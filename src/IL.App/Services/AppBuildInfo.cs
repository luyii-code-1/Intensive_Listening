using System.Reflection;

namespace IL.App.Services;

public static class AppBuildInfo
{
    public const string ReleaseName = "2 Resonance";
    private static readonly string Identity = typeof(AppBuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "2.1.2.4";
    public static string Version => Identity.Split('+')[0];
    public static string DisplayVersion => Version;
    public static string? SuccessfulUpdateVersion { get; set; }
    public static string Commit => Identity.Contains('+') ? Identity.Split('+')[1][..Math.Min(7, Identity.Split('+')[1].Length)] : "local";
    public static string Display => $"{ReleaseName} · 版本 {DisplayVersion} · 构建 {Commit}";
}
