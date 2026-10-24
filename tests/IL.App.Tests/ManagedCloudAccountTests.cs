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
