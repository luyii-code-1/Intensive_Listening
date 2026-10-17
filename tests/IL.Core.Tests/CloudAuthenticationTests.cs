using System.Net;
using System.Text.Json;
using IL.Core.Cloud;
using Xunit;

namespace IL.Core.Tests;

public sealed class CloudAuthenticationTests
{
    [Fact]
    public async Task AuthorizationReadinessRequiresTheRegisteredPkceEndpoints()
    {
        using var http = new HttpClient(new Handler(message =>
        {
            Assert.Equal("/api/v1/auth/desktop-client", message.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"clientId\":\"il-desktop\",\"protocolVersion\":1,\"authorizationEndpoint\":\"/connect/authorize\",\"tokenEndpoint\":\"/connect/token\",\"codeChallengeMethods\":[\"S256\"]}", System.Text.Encoding.UTF8, "application/json") });
        }));
        using var client = new CloudAuthClient(new TokenStore(), http);
        await client.EnsureAuthorizationAvailableAsync();
        Assert.False(client.HasCredentials);
    }

    [Theory]
    [InlineData(404, "application/json", "{\"error\":\"not_found\"}")]
    [InlineData(200, "text/html", "<html><body>Cloud home</body></html>")]
    [InlineData(200, "application/json", "{\"clientId\":\"another-client\"}")]
    [InlineData(200, "application/json", "{")]
    [InlineData(200, "application/json", "{\"clientId\":42}")]
    public async Task MissingAuthorizationServiceReportsAnActionableError(int status, string type, string body)
    {
        var store = new TokenStore();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) {
            Content = new StringContent(body, System.Text.Encoding.UTF8, type) })));
        using var client = new CloudAuthClient(store, http);
        var error = await Assert.ThrowsAsync<CloudAuthenticationException>(() => client.EnsureAuthorizationAvailableAsync());
        Assert.Equal("authorization_unavailable", error.Code);
        Assert.Null(store.Value);
    }

    [Fact]
    public void PkceMatchesRfcVectorAndCallbacksRequireMatchingState()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            CloudAuthorizationRequest.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var request = new CloudAuthorizationRequest(new Uri("http://127.0.0.1:49152/callback"));
        var query = Fields(request.BrowserUri("教室设备").Query.TrimStart('?'));
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("il-desktop", query["client_id"]);
        Assert.Equal("教室设备", query["device_name"]);
        Assert.Equal("sample-code", request.ReadCode(new Uri($"{request.RedirectUri}?code=sample-code&state={query["state"]}")));
        Assert.Equal("invalid_state", Assert.Throws<CloudAuthenticationException>(() => request.ReadCode(new Uri($"{request.RedirectUri}?code=sample-code&state=wrong"))).Code);
        Assert.Equal("access_denied", Assert.Throws<CloudAuthenticationException>(() => request.ReadCode(new Uri($"{request.RedirectUri}?error=access_denied&state={query["state"]}"))).Code);
        Assert.Throws<CloudAuthenticationException>(() => request.ReadCode(new Uri($"{request.RedirectUri}?code=a&code=b&state={query["state"]}")));
        Assert.Throws<ArgumentException>(() => new CloudAuthorizationRequest(new Uri("http://localhost:49152/callback")));
    }

    [Fact]
    public async Task LoginExchangesMatchingVerifierAndPersistsOnlyRefreshToken()
    {
        var store = new TokenStore();
        var request = new CloudAuthorizationRequest(new Uri("http://127.0.0.1:49152/callback"));
        var browser = Fields(request.BrowserUri("Desktop").Query.TrimStart('?'));
        using var http = new HttpClient(new Handler(async message =>
        {
            Assert.Equal("/connect/token", message.RequestUri!.AbsolutePath);
            var form = Fields(await message.Content!.ReadAsStringAsync());
            Assert.Equal(browser["code_challenge"], CloudAuthorizationRequest.Challenge(form["code_verifier"]));
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal(request.RedirectUri.AbsoluteUri, form["redirect_uri"]);
            Assert.Equal("sample-code", form["code"]);
            Assert.False(form.ContainsKey("client_secret"));
            return Token("access-one", "refresh-one");
        }));
        using var client = new CloudAuthClient(store, http);
        await client.CompleteLoginAsync(request, new Uri($"{request.RedirectUri}?code=sample-code&state={browser["state"]}"));
        Assert.Equal("refresh-one", store.Value);
        Assert.Equal("access-one", await client.GetAccessTokenAsync());
    }

    [Fact]
    public async Task ConcurrentAccessRequestsRefreshOnceAndAtomicallySaveRotatedToken()
    {
        var store = new TokenStore { Value = "refresh-old" };
        var calls = 0;
        using var http = new HttpClient(new Handler(async message =>
        {
            Interlocked.Increment(ref calls);
            var form = Fields(await message.Content!.ReadAsStringAsync());
            Assert.Equal("refresh_token", form["grant_type"]);
            Assert.Equal("refresh-old", form["refresh_token"]);
            await Task.Yield();
            return Token("access-new", "refresh-new");
        }));
        using var client = new CloudAuthClient(store, http);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetAccessTokenAsync()));
        Assert.All(tokens, token => Assert.Equal("access-new", token));
        Assert.Equal(1, calls);
        Assert.Equal("refresh-new", store.Value);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task RevokedRefreshClearsCredentialsAndRequiresNewAuthorization()
    {
        var store = new TokenStore { Value = "revoked-refresh" };
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":\"invalid_grant\"}") })));
        using var client = new CloudAuthClient(store, http);
        var error = await Assert.ThrowsAsync<CloudAuthenticationException>(() => client.GetAccessTokenAsync());
        Assert.Equal("login_required", error.Code);
        Assert.False(client.HasCredentials);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task LostRefreshResponseRequiresAuthorizationInsteadOfReplayingOldToken()
    {
        var store = new TokenStore { Value = "refresh-in-flight" };
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("Simulated lost response")));
        using var client = new CloudAuthClient(store, http);
        Assert.Equal("login_required", (await Assert.ThrowsAsync<CloudAuthenticationException>(() => client.GetAccessTokenAsync())).Code);
        Assert.Null(store.Value);
        Assert.False(client.HasCredentials);
    }

    [Fact]
    public async Task LogoutClearsLocalCredentialsEvenWhenRevocationIsUnavailable()
    {
        var store = new TokenStore { Value = "refresh-to-revoke" };
        using var http = new HttpClient(new Handler(async message =>
        {
            Assert.Equal("/connect/revoke", message.RequestUri!.AbsolutePath);
            Assert.Equal("refresh-to-revoke", Fields(await message.Content!.ReadAsStringAsync())["token"]);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));
        using var client = new CloudAuthClient(store, http);
        await Assert.ThrowsAsync<CloudAuthenticationException>(() => client.SignOutAsync());
        Assert.False(client.HasCredentials);
        Assert.Null(store.Value);
    }

    private static HttpResponseMessage Token(string access, string refresh) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, token_type = "Bearer", expires_in = 900 }))
    };
    private static Dictionary<string, string> Fields(string input) => input.Split('&').Select(item => item.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => run(request);
    }
    private sealed class TokenStore : ICloudTokenStore
    {
        public string? Value;
        public int Writes;
        public Task<string?> ReadAsync(CancellationToken ct = default) => Task.FromResult(Value);
        public Task WriteAsync(string refreshToken, CancellationToken ct = default) { Writes++; Value = refreshToken; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct = default) { Value = null; return Task.CompletedTask; }
    }
}
