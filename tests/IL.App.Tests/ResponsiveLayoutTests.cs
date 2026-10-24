using Avalonia;
using Avalonia.Controls;
using IL.App.Views;
using Xunit;

namespace IL.App.Tests;

public sealed class ResponsiveLayoutTests
{
    [Fact]
    public void ActionsMoveBelowContentAndReturnBesideItWhenResized()
    {
        var content = new Control { Width = 200, Height = 48 };
        var action = new Control { Width = 140, Height = 32 };
        var panel = new ResponsivePair(content, action);
        Arrange(panel, 720);
        Assert.True(action.Bounds.Top < content.Bounds.Bottom);
        Assert.True(action.Bounds.Left >= content.Bounds.Right);
        Arrange(panel, 320);
        Assert.True(action.Bounds.Top >= content.Bounds.Bottom + panel.Gap);
        Assert.True(action.Bounds.Right <= 320);
        Arrange(panel, 1024);
        Assert.True(action.Bounds.Top < content.Bounds.Bottom);
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(720, 560)]
    [InlineData(1024, 600)]
    [InlineData(1440, 900)]
    public void PlayerAndTranscriptKeepPositiveSpaceInsideViewport(double width, double height)
    {
        var player = new Control(); var transcript = new Control();
        var panel = new StudentSplitPanel(player, transcript);
        panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height));
        Assert.True(player.Bounds.Height > 0); Assert.True(transcript.Bounds.Height > 0);
        Assert.True(player.Bounds.Right <= width); Assert.True(transcript.Bounds.Right <= width);
        Assert.True(transcript.Bounds.Bottom <= height);
        if (width < 900) Assert.True(transcript.Bounds.Height >= height * .4);
        else Assert.True(transcript.Bounds.Left > player.Bounds.Right);
    }

    private static void Arrange(Control control, double width)
    {
        control.Measure(new Size(width, double.PositiveInfinity));
        control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
    }
}
