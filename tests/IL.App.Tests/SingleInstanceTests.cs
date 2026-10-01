using IL.App.Services;
using Xunit;

namespace IL.App.Tests;

public sealed class SingleInstanceTests
{
    // A mutex must be acquired and released on the same thread.
#pragma warning disable xUnit1031
    [Fact]
    public void ActiveInstanceForwardsUnicodeFileOpenAndReleasesOwnershipOnExit()
    {
        var name = "il2-test-" + Guid.NewGuid().ToString("N");
        using (var owner = SingleInstanceSession.Acquire(name))
        {
            Assert.NotNull(owner);
            var duplicateAcquired = false;
            var thread = new Thread(() => { using var duplicate = SingleInstanceSession.Acquire(name); duplicateAcquired = duplicate != null; });
            thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); Assert.False(duplicateAcquired);
            var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var args = new[] { "C:\\听力课程\\期末 考试.ilp" };
            Assert.True(SingleInstanceSession.NotifyAsync(name, args).GetAwaiter().GetResult());
            owner!.SetActivationHandler(value => received.TrySetResult(value));
            Assert.Equal(args, received.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }
        using var next = SingleInstanceSession.Acquire(name); Assert.NotNull(next);
    }
#pragma warning restore xUnit1031
}
