using System.Security.Cryptography;
using System.Text;
using IL.App.Services;
using IL.Core.Management;
using IL.Core.Updates;
using Xunit;

namespace IL.App.Tests;

public sealed class ManagedUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il-managed-updates-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048);
    private ManagedPolicy _policy = new();
    private int _downloads;
    private ManagedUpdate Offer(string mode = "cold", string version = "2.0.1")
    {
        var task = new ManagedUpdate(Guid.NewGuid().ToString(), mode, version, "http://127.0.0.1:9/setup.exe", Convert.ToHexString(SHA256.HashData("MZ!"u8)), 3, "");
        return task with { Signature = Convert.ToBase64String(_key.SignData(Encoding.UTF8.GetBytes(task.SignedContent), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
    }
    private ManagedUpdater Create(Func<ManagedUpdate, CancellationToken, Task<string>>? download = null)
    {
        var config = new MdnConfiguration { ServerUrl = "http://127.0.0.1:9/", DisableSsl = true, UpdatePublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()) };
        return new(() => _policy, () => config, _ => null, _root, "2.0.1", download ?? (async (_, ct) =>
        {
            _downloads++; Directory.CreateDirectory(_root);
            var file = Path.Combine(_root, "package.exe"); await File.WriteAllTextAsync(file, "MZ!", ct); return file;
        }));
    }
    public void Dispose() { _key.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task ColdSameVersionDownloadsBeforeNoticeAndWaitsForClickToInstall()
    {
        _policy = new() { Update = Offer() };
        using (var client = Create())
        {
            var notices = 0; client.NotificationRequested += _ => notices++;
            await client.RefreshAsync(); await client.RefreshAsync();
            Assert.Equal(1, notices); Assert.Equal(1, _downloads); Assert.True(client.CanUpdate); Assert.True(client.CanInstall);
            Assert.True(await client.PrepareAsync()); Assert.Equal(1, _downloads); Assert.True(client.CanInstall);
        }
        using var restarted = Create(); var repeated = 0; restarted.NotificationRequested += _ => repeated++;
        await restarted.RefreshAsync(); Assert.Equal(0, repeated); Assert.True(restarted.CanUpdate);
    }

    [Fact]
    public async Task SameVersionOtaAttemptsOnceAndNewTaskMayRunAgain()
    {
        _policy = new() { Update = Offer("ota") };
        using var client = Create(); var attempts = 0;
        client.AutomaticInstall = async () => { attempts++; Assert.True(await client.PrepareAsync()); };
        await client.RefreshAsync(); await client.RefreshAsync();
        Assert.Equal(1, attempts); Assert.Equal(1, _downloads);
        using (var restarted = Create())
        {
            restarted.AutomaticInstall = () => { attempts++; return Task.CompletedTask; };
            await restarted.RefreshAsync(); Assert.Equal(1, attempts);
        }
        _policy = new() { Update = Offer("ota") }; await client.RefreshAsync();
        Assert.Equal(2, attempts); Assert.Equal(2, _downloads);
    }
    [Fact]
    public async Task OtaRetriesDownloadFailureBeforeCountingAnInstallationAttempt()
    {
        _policy = new() { Update = Offer("ota") }; var downloads = 0; var installations = 0;
        using var client = Create((_, _) => ++downloads == 1 ? Task.FromException<string>(new IOException()) : Task.FromResult("verified.exe"));
        client.AutomaticInstall = () => { installations++; return Task.CompletedTask; };
        await client.RefreshAsync(); Assert.Equal(0, installations);
        await client.RefreshAsync(); await client.RefreshAsync();
        Assert.Equal(2, downloads); Assert.Equal(1, installations);
    }

    [Fact]
    public async Task CompletedTaskDoesNotOfferOrNotifyAgainAndDowngradesStayBlocked()
    {
        var task = Offer("ota"); _policy = new() { Update = task };
        using var client = Create(); var notices = 0; var attempts = 0;
        client.NotificationRequested += _ => notices++;
        client.AutomaticInstall = () => { attempts++; return Task.CompletedTask; };
        client.RecordResult(task.Id, task.Version, true); await client.RefreshAsync();
        Assert.False(client.HasUpdate); Assert.Equal(0, notices); Assert.Equal(0, attempts);
        _policy = new() { Update = Offer("ota", "2.0.0") }; await client.RefreshAsync();
        Assert.False(client.HasUpdate); Assert.Equal(0, notices); Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task InvalidSignatureAndDisabledUpdatesNeverNotifyOrDownload()
    {
        _policy = new() { Update = Offer() with { Signature = Convert.ToBase64String(new byte[256]) } };
        using var client = Create(); var notices = 0; client.NotificationRequested += _ => notices++;
        await client.RefreshAsync(); Assert.False(client.HasUpdate);
        _policy = new() { Update = Offer(), DisabledFeatures = ["updates"] }; await client.RefreshAsync();
        Assert.False(client.HasUpdate); Assert.Equal(0, notices); Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task ChangedPolicyDuringDownloadPreventsInstallationAndFailureCanRetry()
    {
        _policy = new() { Update = Offer() };
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        using (var client = Create(async (_, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return "unused.exe"; }))
        {
            var preparing = client.RefreshAsync(); await entered.Task;
            Assert.True(client.IsBusy); Assert.False(client.CanUpdate);
            _policy = new(); release.SetResult(); await preparing; Assert.False(client.CanInstall);
        }
        _policy = new() { Update = Offer() }; var calls = 0;
        using var retry = Create((_, _) => ++calls == 1 ? Task.FromException<string>(new IOException()) : Task.FromResult("verified.exe"));
        await retry.RefreshAsync(); Assert.Contains("失败", retry.Status); Assert.Equal(1, calls);
        await retry.RefreshAsync(); Assert.True(retry.CanUpdate);
        Assert.True(await retry.PrepareAsync()); Assert.True(retry.CanInstall);
    }

    [Fact]
    public async Task OtaHasNoSystemNoticeAndCanBeDeferredUntilWindowClose()
    {
        _policy = new() { Update = Offer("ota") };
        using var client = Create(); var notices = 0;
        client.NotificationRequested += _ => notices++;
        client.AutomaticInstall = () => { client.DeferUntilClose(); return Task.CompletedTask; };
        await client.RefreshAsync();
        Assert.Equal(0, notices); Assert.True(client.CanInstallDeferred);
        Assert.False(client.ReopenAfterUpdate); Assert.False(client.CanUpdate);
        _policy = new(); await client.RefreshAsync(); Assert.False(client.CanInstallDeferred);
    }
}
