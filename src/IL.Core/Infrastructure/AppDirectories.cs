using System.Text.Json;

namespace IL.Core.Infrastructure;

public static class AppDirectories
{
    public static string ApplicationDataDirectory()
    {
        var directory = OperatingSystem.IsWindows() ? Path.Combine(RootDirectory(), "application data") : Path.Combine(RootDirectory(), "application");
        Directory.CreateDirectory(directory); return directory;
    }
    public static Task<long> ClearCacheAsync()
    {
        var cache = Path.Combine(DataDirectory(), "cache");
        var bytes = Directory.Exists(cache) ? Directory.EnumerateFiles(cache,"*",SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;
        if (Directory.Exists(cache)) Directory.Delete(cache, true);
        return Task.FromResult(bytes);
    }
    public static string RootDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var standalone = Environment.GetEnvironmentVariable("ILP_STANDALONE_DATA")?.Trim();
            if (!string.IsNullOrEmpty(standalone)) return standalone;
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "Intensive Listening");
        }
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home)) return Path.Combine(home, "Library", "Application Support", "Intensive Listening");
        }
        return Path.Combine(Environment.CurrentDirectory, ".intensive_listening");
    }
    public static string DataDirectory()
    {
        var root = RootDirectory();
        var standalone = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ILP_STANDALONE_DATA")?.Trim() : null;
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(standalone)) { Directory.CreateDirectory(root); return root; }
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        foreach (var name in new[] { "settings.json", "lesson_progress.json", "transcription_queue.json", "library", "projects", "mcp" })
        {
            var source = Path.Combine(root, name);
            var target = Path.Combine(data, name);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            if (File.Exists(source)) File.Move(source, target);
            else if (Directory.Exists(source)) Directory.Move(source, target);
        }
        var application = Path.Combine(root, "application data");
        Directory.CreateDirectory(application);
        File.WriteAllText(Path.Combine(application, "version.json"), JsonSerializer.Serialize(new { appVersion = "2.0.0-dev", dataSchemaVersion = 1 }));
        return data;
    }
}
