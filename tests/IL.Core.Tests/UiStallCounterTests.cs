using IL.Core.Telemetry;
using Xunit;
namespace IL.Core.Tests;
public class UiStallCounterTests
{
    private static bool Sample(UiStallCounter counter, double seconds, double delay = 150) => counter.Record(TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(delay));
    [Fact] public void StartupAndIsolatedStallsDoNotTriggerReduction()
    {
        var c = new UiStallCounter();
        Assert.False(Sample(c, 1)); Assert.False(Sample(c, 2)); Assert.False(Sample(c, 3));
        Assert.False(Sample(c, 11)); Assert.False(Sample(c, 22)); Assert.False(Sample(c, 33));
        Assert.Equal(6, c.Snapshot.Stalls);
    }
    [Fact] public void SustainedStallsReduceOncePerCooldown()
    {
        var c = new UiStallCounter();
        Assert.False(Sample(c, 11)); Assert.False(Sample(c, 12)); Assert.True(Sample(c, 13));
        Assert.False(Sample(c, 14)); Assert.False(Sample(c, 15)); Assert.False(Sample(c, 16));
        Assert.False(Sample(c, 43)); Assert.False(Sample(c, 44)); Assert.True(Sample(c, 45));
    }
    [Fact] public void ResumingClearsOldStallsAndHasWarmup()
    {
        var c = new UiStallCounter(); Sample(c, 11); Sample(c, 12);
        c.ResetDetection(TimeSpan.FromSeconds(13));
        Assert.False(Sample(c, 14)); Assert.False(Sample(c, 15)); Assert.False(Sample(c, 16));
        Assert.False(Sample(c, 24)); Assert.False(Sample(c, 25)); Assert.True(Sample(c, 26));
    }
    [Fact] public void HealthyDispatchAndThresholdBoundaryDoNotTrigger()
    {
        var c = new UiStallCounter();
        for (var i = 10; i < 100; i++) Assert.False(Sample(c, i, 100));
        Assert.Equal(90, c.Snapshot.Samples); Assert.Equal(0, c.Snapshot.Stalls);
        Assert.Equal(100d, c.Snapshot.PeakDelayMs);
    }
}
