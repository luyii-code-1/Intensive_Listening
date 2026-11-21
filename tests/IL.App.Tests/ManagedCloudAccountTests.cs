using System.Net;
using System.Text;
using System.Text.Json;
using IL.App.Services;
using IL.Core.Cloud;
using IL.Core.Management;
using Xunit;
namespace IL.App.Tests;

public sealed class ManagedCloudAccountTests
{
    [Fact]
    public async Task FailedIdentityLookupKeepsThePreviousGrantAndRestartVerifiesItBeforePublishing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-cloud-reauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new AccountSwitchHandler();
            using var http = new HttpClient(handler);
            var credentials = new Tokens { Token = "refresh-A" };
            var authorization = new CloudAuthorizationRequest(new Uri("http://127.0.0.1:49152/callback"));
            var state = authorization.BrowserUri("Desktop").Query.Split('&').Single(field => field.StartsWith("state="));
            var callback = new Uri(authorization.RedirectUri + "?code=switch&" + state);
            string? firstSession;
            using (var account = new CloudAccountService(root, credentials, http))
            {
                await account.ApplyEnabledAsync(true);
                firstSession = account.ManagementSnapshot.SessionId;
                handler.IdentityUnavailable = true;
                await Assert.ThrowsAsync<HttpRequestException>(() => account.CompleteAuthorizationAsync(authorization, callback, default));
                Assert.True(account.IsSignedIn);
                Assert.Equal("A", account.ManagementSnapshot.Account!.Id);
                Assert.Equal(firstSession, account.ManagementSnapshot.SessionId);
                Assert.Equal("refresh-A", credentials.Token);
                Assert.Equal("access-A", await account.Authentication.GetAccessTokenAsync());
            }
            using var restart = new CloudAccountService(root, credentials, http);
            await restart.ApplyEnabledAsync(true);
            Assert.False(restart.IsSignedIn);
            Assert.Null(restart.ManagementSnapshot.Account);
            handler.IdentityUnavailable = false;
            await restart.RestoreAsync();
            Assert.Equal(firstSession, restart.ManagementSnapshot.SessionId);
            await restart.CompleteAuthorizationAsync(authorization, callback, default);
            var secondSession = restart.ManagementSnapshot.SessionId;
            Assert.NotEqual(firstSession, secondSession);
            Assert.Equal("B", restart.ManagementSnapshot.Account!.Id);
            Assert.Equal("refresh-B", credentials.Token);
            using var afterSwitch = new CloudAccountService(root, credentials, http);
            await afterSwitch.ApplyEnabledAsync(true);
            Assert.Equal(secondSession, afterSwitch.ManagementSnapshot.SessionId);
            Assert.Equal("B", afterSwitch.ManagementSnapshot.Account!.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class AccountSwitchHandler : HttpMessageHandler
    {
        public bool IdentityUnavailable;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/connect/token":
                    var form = await request.Content!.ReadAsStringAsync(ct);
                    var id = form.Contains("grant_type=authorization_code") || form.Contains("refresh-B") ? "B" : "A";
                    body = new { access_token = "access-" + id, refresh_token = "refresh-" + id, token_type = "Bearer", expires_in = 900 };
                    break;
                case "/api/v1/auth/me":
                    if (IdentityUnavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    var owner = request.Headers.Authorization!.Parameter == "access-A" ? "A" : "B";
                    body = new { id = owner, email = owner + "@example.invalid", name = owner, slots = 1, emailVerified = true };
                    break;
                case "/connect/revoke":
                    body = new { };
                    break;
                default:
                    throw new InvalidOperationException("Unexpected endpoint");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task WindowsLogoutCommandIsSessionBoundAndReplayDoesNotLogoutNewAccount()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-cloud-account-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient(new Handler()); var credentials = new Tokens(); string oldSession;
            var commandId = Guid.NewGuid().ToString("D");
            using (var account = new CloudAccountService(root, credentials, http))
            {
                await account.ApplyEnabledAsync(true); oldSession = account.ManagementSnapshot.SessionId!;
                Assert.True(Guid.TryParse(oldSession, out _));
                ManagedCloudReceipt? receipt = null; account.CommandCompleted += (_, result) => receipt = result;
                await account.ExecuteAsync(new(Guid.NewGuid().ToString("D"), "logout", Guid.NewGuid().ToString("D"), 1));
                Assert.Equal("skipped", receipt!.Status); Assert.NotNull(credentials.Token);
                await account.ExecuteAsync(new(commandId, "logout", oldSession, 1));
                Assert.Equal("completed", receipt.Status); Assert.Null(credentials.Token);
            }
            credentials.Token = "new-authorization";
            using var restored = new CloudAccountService(root, credentials, http);
            await restored.ApplyEnabledAsync(true);
            Assert.NotEqual(oldSession, restored.ManagementSnapshot.SessionId);
            await restored.ExecuteAsync(new(commandId, "logout", oldSession, 1));
            Assert.NotNull(credentials.Token); Assert.True(restored.IsSignedIn);
            var secondSession = restored.ManagementSnapshot.SessionId;
            using var restart = new CloudAccountService(root, credentials, http);
            await restart.ApplyEnabledAsync(true);
            Assert.Equal(secondSession, restart.ManagementSnapshot.SessionId);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object value = request.RequestUri!.AbsolutePath switch
            {
                "/connect/token" => new { access_token = "access", refresh_token = "rotated", token_type = "Bearer", expires_in = 900 },
                "/api/v1/auth/me" => new { id = "user", email = "user@example.invalid", name = "User", slots = 1, emailVerified = true },
                "/connect/revoke" => new { },
                _ => throw new InvalidOperationException("Unexpected endpoint")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Tokens : ICloudTokenStore
    {
        public string? Token = "refresh";
        public Task<string?> ReadAsync(CancellationToken ct = default) => Task.FromResult(Token);
        public Task WriteAsync(string token, CancellationToken ct = default) { Token = token; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct = default) { Token = null; return Task.CompletedTask; }
    }
}
