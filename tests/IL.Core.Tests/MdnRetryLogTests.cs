using IL.Core.Infrastructure;
using IL.Core.Management;
using Xunit;

namespace IL.Core.Tests;

public sealed class MdnRetryLogTests
{
    [Fact]
    public void OutageWritesOneWarningAndRecoveryAllowsTheNextOutage()
    {
        var operation = "mdn-retry-" + Guid.NewGuid().ToString("N");
        var log = new MdnClient.RetryLog(operation);
        for (var i = 0; i < 500; i++) log.Failed(new HttpRequestException("offline"));
        var first = Assert.Single(LiveDiagnostics.Snapshot().Where(e => e.Message.StartsWith(operation)));
        Assert.Equal("WARN", first.Level);
        Assert.Contains("HttpRequestException", first.Message);

        log.Recovered();
        log.Recovered();
        Assert.Single(LiveDiagnostics.Snapshot().Where(e => e.Message.StartsWith(operation + "已恢复")));
        log.Failed(new HttpRequestException("offline again"));
        Assert.Equal(2, LiveDiagnostics.Snapshot().Count(e => e.Level == "WARN" && e.Message.StartsWith(operation)));
    }
}
