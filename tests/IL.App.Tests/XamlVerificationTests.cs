using IL.App.Services;
using Xunit;

namespace IL.App.Tests;

public sealed class XamlVerificationTests
{
    [Fact]
    public void ApplicationContainsCompiledXamlAndResources() => XamlVerification.Validate();

    [Fact]
    public void MissingCompiledXamlIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => XamlVerification.Validate(typeof(XamlVerificationTests)));
        Assert.Contains("Precompiled XAML is missing", error.Message);
    }
}
