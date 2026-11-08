using IL.Core.Settings;
using System.Text.Json.Nodes;
using Xunit;
namespace IL.Core.Tests;
public class AnimationLevelTests
{
    [Theory]
    [InlineData(AnimationLevel.Full)]
    [InlineData(AnimationLevel.Reduced)]
    [InlineData(AnimationLevel.Off)]
    public void LevelSurvivesSettingsRoundTrip(AnimationLevel level)
    {
        var settings = AppSettings.Defaults() with { AnimationLevel = level };
        Assert.Equal(level, AppSettings.FromJson(settings.ToJson()).AnimationLevel);
    }
    [Fact]
    public void ExistingPerformancePreferenceMigratesToOff()
    {
        Assert.Equal(AnimationLevel.Off, AppSettings.FromJson(new JsonObject { ["performanceMode"] = true }).AnimationLevel);
        Assert.Equal(AnimationLevel.Full, AppSettings.FromJson(new JsonObject()).AnimationLevel);
    }
}
