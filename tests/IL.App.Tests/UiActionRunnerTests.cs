using IL.App.Services;
using IL.Core.Management;
using Xunit;

namespace IL.App.Tests;

public sealed class UiActionRunnerTests
{
    [Fact]
    public async Task DenialAfterAnAwaitIsReturnedAndAnotherActionCanStillRun()
    {
        var expected = new ManagedFeatureDeniedException("importIlp");
        var result = await UiActionRunner.RunAsync(async () => { await Task.Yield(); throw expected; });
        Assert.Same(expected, result);
        Assert.Equal("importIlp", ((ManagedFeatureDeniedException)result!).Feature);
        var completed = false;
        Assert.Null(await UiActionRunner.RunAsync(() => { completed = true; return Task.CompletedTask; }));
        Assert.True(completed);
    }

    [Fact]
    public async Task FailedAndCancelledTasksDoNotEscapeTheEventBoundary()
    {
        var error = new IOException("file failed");
        Assert.Same(error, await UiActionRunner.RunAsync(() => Task.FromException(error)));
        Assert.Null(await UiActionRunner.RunAsync(() => Task.FromCanceled(new CancellationToken(true))));
        await Assert.ThrowsAsync<OutOfMemoryException>(() => UiActionRunner.RunAsync(() => Task.FromException(new OutOfMemoryException())));
    }
}
