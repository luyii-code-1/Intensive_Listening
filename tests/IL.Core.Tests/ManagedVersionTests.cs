using IL.Core.Updates;
using Xunit;

namespace IL.Core.Tests;

public sealed class ManagedVersionTests
{
    [Theory]
    [InlineData("2.1.2-insider.20261022.1", "v2.1.2 Insider Preview 1022 1", 0)]
    [InlineData("v2.1.2 Insider Preview 1022 1", "v2.1.2 Insider Preview 1022 2", -1)]
    [InlineData("v2.1.2 Beta 1022", "v2.1.2", -1)]
    [InlineData("2.1.2", "2.1.2.0", 0)]
    [InlineData("2.1.2", "2.1.1.9", 1)]
    public void ComparesBuildIdentity(string current, string offered, int expected)
    {
        Assert.True(ManagedVersion.TryParse(current, out var left));
        Assert.True(ManagedVersion.TryParse(offered, out var right));
        Assert.Equal(expected, Math.Sign(left.CompareTo(right)));
        Assert.Equal(expected <= 0, ManagedUpdateJournal.AllowsVersion(current, offered));
        Assert.Equal(expected == 0, ManagedUpdateJournal.IsSameVersion(current, offered));
    }
    [Theory]
    [InlineData("v2.1.2 Insider Preview 1022 0")]
    [InlineData("2.1.2-insider.20261022")]
    [InlineData("v2.1.2 Beta 1322")]
    public void RejectsInvalidBuild(string value) => Assert.False(ManagedVersion.TryParse(value, out _));
}
