using Avalonia.Controls;
using Avalonia.Input;
using IL.App.Services;
using IL.Core.Settings;
using Xunit;

namespace IL.App.Tests;

public sealed class PlayerShortcutTests
{
    [Fact]
    public void SpaceWorksWhenFocusReturnsToWindowAndLeavesTextEntryAlone()
    {
        var window = new Grid(); var workspace = new Grid(); window.Children.Add(workspace);
        var play = new Button(); var input = new TextBox(); workspace.Children.Add(play); workspace.Children.Add(input);
        Assert.True(PlayerShortcuts.AllowsSource(window, window, workspace));
        Assert.True(PlayerShortcuts.AllowsSource(play, window, workspace));
        Assert.False(PlayerShortcuts.AllowsSource(input, window, workspace));
        Assert.False(PlayerShortcuts.AllowsSource(new Button(), window, workspace));
        var bindings = new Dictionary<PlayerShortcutAction, string> { [PlayerShortcutAction.TogglePlayback] = "Space" };
        Assert.True(PlayerShortcuts.TryGetAction(bindings, Key.Space, KeyModifiers.None, out var action));
        Assert.Equal(PlayerShortcutAction.TogglePlayback, action);
    }
}
