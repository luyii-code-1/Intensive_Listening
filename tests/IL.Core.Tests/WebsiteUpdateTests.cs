using System.Net;
using System.Text;
using System.Text.Json;
using IL.Core.Updates;
using Xunit;

namespace IL.Core.Tests;

public sealed class WebsiteUpdateTests
{
    [Theory]
    [InlineData("2.1.2.3", "v2.2.0", "available")]
    [InlineData("2.2.0", "v2.2.0", "current")]
    [InlineData("2.2.0.0", "v2.2.0", "current")]
    [InlineData("2.2.1-insider.20261031.2", "v2.2.0", "current")]
    [InlineData("2.2.1-insider.20261031.2", "v2.2.1", "available")]
    [InlineData("2.2.1-beta.20261031", "v2.2.1", "available")]
    [InlineData("2.2.1", "v2.2.2", "available")]
    public async Task OfficialMetadataPreservesStableAndPreviewOrdering(string installed, string tag, string expected)
    {
        var requests = 0;
        using var checker = new WebsiteUpdateChecker(new Handler((request, _) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://il.luyii.cn/downloads/latest.json", request.RequestUri!.AbsoluteUri);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            Assert.True(request.Headers.CacheControl?.NoCache);
            return Task.FromResult(Response(JsonSerializer.Serialize(new { tag, html_url = "https://example.com/installer" })));
        }));

        await checker.CheckAsync(installed);

        Assert.Equal(1, requests);
        Assert.Equal(expected, checker.Result.State);
        Assert.Equal(tag, checker.Result.Version);
        Assert.Equal("https://il.luyii.cn/#download", checker.Result.ReleaseUrl);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tag\":null}")]
    [InlineData("{\"tag\":42}")]
    [InlineData("{\"tag\":\"v2.2.1-insider.20261031.2\"}")]
    [InlineData("{\"tag\":\"v2.2.1-beta.20261031\"}")]
    [InlineData("{\"tag\":\"invalid\"}")]
    [InlineData("<html>Unavailable</html>")]
    public async Task InvalidMetadataReportsFailure(string body)
    {
        using var checker = new WebsiteUpdateChecker(new Handler((_, _) => Task.FromResult(Response(body))));

        await checker.CheckAsync("2.2.0");

        Assert.Equal("failed", checker.Result.State);
        Assert.Null(checker.Result.ReleaseUrl);
    }

    [Fact]
    public async Task FailedCheckCanBeRetriedAgainstTheWebsite()
    {
        var attempts = 0;
        using var checker = new WebsiteUpdateChecker(new Handler((_, _) => Task.FromResult(
            ++attempts == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response("{\"tag\":\"v2.2.1\"}"))));

        await checker.CheckAsync("2.2.0");
        Assert.Equal("failed", checker.Result.State);
        await checker.CheckAsync("2.2.0");
        Assert.Equal("available", checker.Result.State);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ConcurrentCheckKeepsOneRequestAndCancellationReleasesIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var checker = new WebsiteUpdateChecker(new Handler(async (_, token) =>
        {
            requests++;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response("{}");
        }));
        using var cancellation = new CancellationTokenSource();
        var checking = checker.CheckAsync("2.2.0", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await checker.CheckAsync("2.2.0");
        Assert.Equal(1, requests);
        cancellation.Cancel();
        await checking;
        Assert.Equal("failed", checker.Result.State);
        Assert.Equal("更新检查已取消", checker.Result.Message);
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
