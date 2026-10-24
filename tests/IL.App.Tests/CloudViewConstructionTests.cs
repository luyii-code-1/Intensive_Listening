using Avalonia.Controls;
using IL.App.Services;
using IL.App.Views;
using Xunit;

namespace IL.App.Tests;

[CollectionDefinition("Cloud view construction", DisableParallelization = true)]
public sealed class CloudViewConstructionCollection { }

[Collection("Cloud view construction")]
public sealed class CloudViewConstructionTests
{
    [Fact]
    public async Task CloudViewsConstructWithOneParentForTheirContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-cloud-view-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousData = Environment.GetEnvironmentVariable("ILP_STANDALONE_DATA");
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.SetEnvironmentVariable("ILP_STANDALONE_DATA", root);
            if (OperatingSystem.IsMacOS()) Environment.SetEnvironmentVariable("HOME", root);
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) Environment.CurrentDirectory = root;
            await using var services = new AppServices(action => action(), standalone: true);
            AssertContentTree(new CloudSettingsView(services));
            AssertContentTree(new CloudAuthoringView(services, () => Task.CompletedTask, () => Task.CompletedTask));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILP_STANDALONE_DATA", previousData);
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, true);
        }
    }

    private static void AssertContentTree(UserControl view)
    {
        var content = Assert.IsType<StackPanel>(view.Content);
        Assert.Same(view, content.Parent);
        Assert.NotEmpty(content.Children);
    }
}
