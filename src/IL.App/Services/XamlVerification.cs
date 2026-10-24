using System.Reflection;
using System.Text.Json;

namespace IL.App.Services;

public static class XamlVerification
{
    public static void Validate()
    {
        Validate(typeof(App));
        Validate(typeof(MainWindow));
        if (!typeof(App).Assembly.GetManifestResourceNames().Contains("!AvaloniaResources"))
            throw new InvalidOperationException("Application Avalonia resources are missing.");
    }

    public static void Validate(Type type)
    {
        if (type.GetMethod("!XamlIlPopulate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is null)
            throw new InvalidOperationException($"Precompiled XAML is missing for {type.FullName}. Rebuild the application before publishing.");
    }

    public static int Run(string output)
    {
        Exception? error = null;
        try { Validate(); }
        catch (Exception exception) { error = exception; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            success = error is null,
            informationalVersion = AppBuildInfo.UpdateVersion,
            error = error?.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
        return error is null ? 0 : 1;
    }
}
