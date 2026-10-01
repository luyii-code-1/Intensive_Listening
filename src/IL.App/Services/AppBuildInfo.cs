using System.Reflection;

namespace IL.App.Services;

public static class AppBuildInfo
{
    private static readonly string Identity = typeof(AppBuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "2.0.0-dev";
    public static string Version => Identity.Split('+')[0];
    public static string Commit => Identity.Contains('+') ? Identity.Split('+')[1][..Math.Min(7, Identity.Split('+')[1].Length)] : "local";
    public static string Display => $"版本 {Version} · 构建 {Commit}";
}
