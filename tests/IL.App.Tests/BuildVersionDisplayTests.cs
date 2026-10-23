using IL.App.Services;
using Xunit;
namespace IL.App.Tests;
public sealed class BuildVersionDisplayTests
{
    [Theory]
    [InlineData("2.2.0+abcdef1", "v2.2.0")]
    [InlineData("2.2.0-insider.20261021.3+abcdef1", "v2.2.0 Insider Preview 1021 3")]
    [InlineData("2.2.0-beta.20261021+abcdef1", "v2.2.0 Beta 1021")]
    public void DisplaysChannelWithoutExposingBuildMetadata(string identity, string display) => Assert.Equal(display, AppBuildInfo.FormatVersion(identity));
}
