using System.Net;
using IL.App.Services;
using Xunit;

namespace IL.App.Tests;

public sealed class CloudCallbackTests
{
    [Fact]
    public async Task LoopbackIgnoresInvalidStateAndCompletesOnlyMatchingCallback()
    {
        await using var callback = new CloudLoopbackCallback();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completion = callback.WaitAsync(deadline.Token);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var wrong = await http.GetAsync($"{callback.Request.RedirectUri}?code=untrusted&state=wrong", deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.False(completion.IsCompleted);
        var state = callback.Request.BrowserUri("test").Query.TrimStart('?').Split('&')
            .Select(item => item.Split('=', 2)).Single(parts => parts[0] == "state")[1];
        var expected = new Uri($"{callback.Request.RedirectUri}?code=trusted&state={state}");
        using var accepted = await http.GetAsync(expected, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(expected, await completion);
        Assert.Equal("no-store", accepted.Headers.CacheControl!.ToString());
    }
}
