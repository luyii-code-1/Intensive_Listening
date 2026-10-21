using IL.App.Services;
using Xunit;
namespace IL.App.Tests;
public sealed class BuildVersionDisplayTests
{
    [Theory]
    [InlineData("2.4.2+abcdef1", "v2.4.2")]
    [InlineData("2.4.2-insider.20261021.3+abcdef1", "v2.4.2 Insider Preview 1021 3")]
    [InlineData("2.4.2-beta.20261021+abcdef1", "v2.4.2 Beta 1021")]
    public void DisplaysChannelWithoutExposingBuildMetadata(string identity, string display) => Assert.Equal(display, AppBuildInfo.FormatVersion(identity));
}
