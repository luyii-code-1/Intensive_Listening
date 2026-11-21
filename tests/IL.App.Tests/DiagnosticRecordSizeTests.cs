using IL.Core.Infrastructure;
using Xunit;

namespace IL.App.Tests;

[Collection("Diagnostics")]
public sealed class DiagnosticRecordSizeTests
{
    [Fact]
    public void OversizedOperationIsBoundedBeforeBufferingAndRetainsRedactedContext()
    {
        // Place the credential across the retained tail boundary of a multi-megabyte entry.
        var message = "request started\n" + new string('x', 3 * 1024 * 1024) +
            "\nAuthorization: Bearer " + new string('s', 20000) + "\nrequest finished";
        var entry = LiveDiagnostics.Record("WARN", "fixture", message);

        Assert.StartsWith("request started", entry.Message);
        Assert.EndsWith("[redacted]\nrequest finished", entry.Message);
        Assert.DoesNotContain(new string('s', 32), entry.Message);
        Assert.True(entry.Message.Length <= 32 * 1024);
        Assert.Contains(entry, LiveDiagnostics.Snapshot());
        Assert.Contains(entry.Message, LiveDiagnostics.RecentOperations());
    }
}
