using System.Diagnostics;
using System.Text.Json;
using IL.Core.Cloud;
using IL.Core.Infrastructure;
using IL.Core.Management;

namespace IL.App.Services;

public sealed class CloudAccountService : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ICloudTokenStore _credentials;
    private readonly string _statePath;
    private CancellationTokenSource? _login;
    private bool _busy, _disposed, _reauthRequired, _identityVerified;
    private sealed record State(string? SessionId, ManagedCloudAccount? Account,
        Dictionary<string, ManagedCloudReceipt> Receipts, Dictionary<string, string> PendingRevocations);
    private State _state = new(null, null, [], []);
    public CloudAuthClient Authentication { get; }
    public event Action? Changed;
    public event Action<string, ManagedCloudReceipt>? CommandCompleted;
    public string Status { get; private set; } = "云服务已关闭";
    public string Connection { get; private set; } = "disabled";
    public string? ErrorCode { get; private set; }
    public bool IsBusy => _busy;
    public bool Enabled { get; private set; }
    private bool HasVerifiedSession => _identityVerified && Authentication.HasCredentials && !_reauthRequired;
    public bool IsSignedIn => Enabled && HasVerifiedSession;
    public bool AccountLocked => ManagedRuntime.IsManaged && ManagedRuntime.Policy.CloudService.LockAccount;
    public bool CanAuthorize => Enabled && !_busy && (!AccountLocked || !HasVerifiedSession);
    public bool CanSignOut => !_busy && !AccountLocked && _state.SessionId is not null;
    public ManagedCloudSnapshot ManagementSnapshot => new(ManagedRuntime.Policy.Revision, Enabled,
        _login is not null ? "authorizing" : _reauthRequired ? "reauthRequired" : HasVerifiedSession ? "signedIn" : "signedOut",
        Enabled ? Connection : "disabled",
        HasVerifiedSession && _login is null ? _state.Account : null,
        HasVerifiedSession && _login is null ? _state.SessionId : null, ErrorCode);

    public CloudAccountService(string? dataDirectory = null, ICloudTokenStore? credentials = null, HttpClient? http = null)
    {
        _credentials = credentials ?? new CloudCredentialStore();
        Authentication = new(_credentials, http); Authentication.ConnectivityChanged += ConnectivityChanged;
        _statePath = Path.Combine(dataDirectory ?? AppDirectories.DataDirectory(), "cloud-account.bin");
        if (File.Exists(_statePath))
            try { _state = JsonSerializer.Deserialize<State>(ManagedSecretStore.Load(_statePath), ManagedPolicy.Json) ?? _state; }
            catch (Exception error) { ErrorCode = "credentialStore"; Status = "无法读取云服务账号状态。"; AppLog.Warning("云账号状态读取失败", error); }
    }
    private void Save() => ManagedSecretStore.Save(_statePath, JsonSerializer.Serialize(_state, ManagedPolicy.Json));
    public async Task ApplyEnabledAsync(bool enabled)
    {
        if (ManagedRuntime.IsManaged && ManagedRuntime.Policy.CloudService.Mode != "followLocal")
            enabled = ManagedRuntime.Policy.CloudService.Mode == "enabled";
        if (Enabled == enabled) return;
        Enabled = enabled;
        if (!enabled) { CancelLogin(); Connection = "disabled"; Status = "云服务已关闭"; Notify(); return; }
        await RestoreAsync();
    }
    public async Task RestoreAsync()
    {
        if (!Enabled || !await _operations.WaitAsync(0)) return;
        _busy = true;
        try
        {
            Connection = "connecting"; Notify();
            await Authentication.RestoreAsync(_lifetime.Token);
            if (Authentication.HasCredentials)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var user = await Authentication.GetUserAsync(deadline.Token);
                try { _state = SaveIdentity(user, newGeneration: false); }
                catch (CloudAuthenticationException error) when (error.Code == "account_locked")
                {
                    await Authentication.SignOutAsync(CancellationToken.None);
                    throw;
                }
                _identityVerified = true;
                _reauthRequired = false;
                Status = "已登录 Intensive Listening 云服务"; Connection = "connected"; ErrorCode = null;
            }
            else { Status = ManagedRuntime.Policy.CloudService.RequireLogin ? "您的组织要求登录云服务。" : "未登录 Intensive Listening 云服务"; Connection = "connected"; }
            await RetryRevocationsAsync();
        }
        catch (Exception error) { Present(error); }
        finally { _busy = false; _operations.Release(); Notify(); }
    }
    private State SaveIdentity(CloudUser user, bool newGeneration)
    {
        if (AccountLocked && _state.Account is { } bound && bound.Id != user.Id)
            throw new CloudAuthenticationException("account_locked", "您的组织已锁定原账号，请使用原账号授权。");
        var session = newGeneration || _state.Account?.Id != user.Id ? null : _state.SessionId;
        var next = _state with { SessionId = session ?? Guid.NewGuid().ToString("D"), Account = new(user.Id, user.Email, user.Name) };
        ManagedSecretStore.Save(_statePath, JsonSerializer.Serialize(next, ManagedPolicy.Json));
        return next;
    }

    internal async Task CompleteAuthorizationAsync(CloudAuthorizationRequest request, Uri callback, CancellationToken ct)
    {
        State? verified = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await Authentication.CompleteLoginAsync(request, callback, deadline.Token, user =>
        {
            // Persist the new generation before replacing credentials. After an interrupted write,
            // restoration verifies the stored token's owner before exposing a session.
            verified = SaveIdentity(user, newGeneration: true);
            _identityVerified = false;
        });
        _state = verified!;
        _identityVerified = true;
        _reauthRequired = false;
    }
    public async Task LoginAsync()
    {
        if (!CanAuthorize || !await _operations.WaitAsync(0)) return;
        _busy = true;
        using var login = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _login = login;
        try
        {
            Status = "正在连接登录服务…"; Connection = "connecting"; ErrorCode = null; Notify();
            await Authentication.EnsureAuthorizationAvailableAsync(login.Token);
            await using var callback = new CloudLoopbackCallback();
            Status = "请在浏览器中登录并允许授权…"; Notify();
            Process.Start(new ProcessStartInfo(callback.Request.BrowserUri(Environment.MachineName).AbsoluteUri) { UseShellExecute = true });
            await CompleteAuthorizationAsync(callback.Request, await callback.WaitAsync(login.Token), login.Token);
            Status = "已登录 Intensive Listening 云服务"; Connection = "connected"; ErrorCode = null;
        }
        catch (Exception error) { Present(error, invalidateSession: !HasVerifiedSession); }
        finally { _login = null; _busy = false; _operations.Release(); Notify(); }
    }
    public void CancelLogin() => _login?.Cancel();
    public void AuthenticationFailed(string message)
    { _reauthRequired = true; ErrorCode = "authorization"; Status = message; Connection = Enabled ? "error" : "disabled"; Notify(); }
    public async Task SignOutAsync()
    {
        if (!CanSignOut || !await _operations.WaitAsync(0)) return;
        _busy = true;
        try { await ClearCredentialsAsync(Guid.NewGuid().ToString("D")); Status = "已退出云服务"; }
        catch (Exception error) { Present(error); }
        finally { _busy = false; _operations.Release(); Notify(); }
    }
    public async Task ApplyManagementAsync()
    {
        if (await _operations.WaitAsync(0))
        {
            try { await Authentication.RestoreAsync(_lifetime.Token); }
            finally { _operations.Release(); }
        }
        foreach (var command in ManagedRuntime.Policy.CloudCommands) await ExecuteAsync(command);
        if (Enabled && await _operations.WaitAsync(0))
        {
            try { await RetryRevocationsAsync(); }
            finally { _operations.Release(); }
        }
        Notify();
    }
    internal async Task ExecuteAsync(ManagedCloudCommand command)
    {
        CancelLogin();
        await _operations.WaitAsync(); _busy = true;
        try
        {
            if (_state.Receipts.TryGetValue(command.Id, out var completed)) { CommandCompleted?.Invoke(command.Id, completed); return; }
            ManagedCloudReceipt receipt;
            if (_state.SessionId != command.TargetSessionId) receipt = new("skipped", "notRequired", "sessionMismatch");
            else
                try { var revoked = await ClearCredentialsAsync(command.Id); receipt = new("completed", revoked ? "revoked" : "pending"); }
                catch (Exception) { receipt = new("failed", "notRequired", "credentialStore"); }
            _state.Receipts[command.Id] = receipt; Save(); CommandCompleted?.Invoke(command.Id, receipt);
        }
        finally { _busy = false; _operations.Release(); Notify(); }
    }
    private async Task<bool> ClearCredentialsAsync(string operation)
    {
        var token = await _credentials.ReadAsync(CancellationToken.None) ?? _state.PendingRevocations.GetValueOrDefault(operation);
        if (token is not null) { _state.PendingRevocations[operation] = token; Save(); }
        var revoked = true;
        try { await Authentication.SignOutAsync(CancellationToken.None); if (token is not null) await Authentication.RevokeRefreshTokenAsync(token, CancellationToken.None); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or CloudAuthenticationException { Code: "revocation_failed" }) { revoked = false; }
        _state = _state with { SessionId = null, Account = null };
        _identityVerified = false;
        if (revoked) _state.PendingRevocations.Remove(operation);
        _state.Receipts[operation] = new("completed", revoked ? "revoked" : "pending");
        _reauthRequired = false; ErrorCode = null; Connection = Enabled ? "connected" : "disabled";
        Save(); Status = revoked ? "已退出云服务" : "已退出本机账号，云端撤销待重试";
        return revoked;
    }
    private async Task RetryRevocationsAsync()
    {
        foreach (var pending in _state.PendingRevocations.ToArray())
            try
            {
                await Authentication.RevokeRefreshTokenAsync(pending.Value, _lifetime.Token);
                _state.PendingRevocations.Remove(pending.Key);
                if (_state.Receipts.TryGetValue(pending.Key, out var receipt) && receipt.Status == "completed")
                { receipt = receipt with { Revocation = "revoked" }; _state.Receipts[pending.Key] = receipt; Save(); CommandCompleted?.Invoke(pending.Key, receipt); }
                else Save();
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or CloudAuthenticationException) { }
    }
    private void ConnectivityChanged(bool connected)
    {
        if (!Enabled) return;
        var connection = connected ? "connected" : "offline";
        var errorCode = connected ? null : "network";
        if (Connection == connection && ErrorCode == errorCode) return;
        Connection = connection; ErrorCode = errorCode;
        if (!connected) Status = "暂时无法连接云服务，请稍后重试。";
        else if (IsSignedIn) Status = "已登录 Intensive Listening 云服务";
        Notify();
    }
    private void Present(Exception error, bool invalidateSession = true)
    {
        ErrorCode = error is CloudAuthenticationException ? "authorization" : error is HttpRequestException or OperationCanceledException ? "network" : "credentialStore";
        Connection = !Enabled ? "disabled" : ErrorCode == "network" ? "offline" : "error";
        if (invalidateSession && error is CloudAuthenticationException) _reauthRequired = true;
        Status = error is CloudAuthenticationException ? error.Message : error is OperationCanceledException ? "授权已取消或连接超时，可重试。" : error is HttpRequestException ? "暂时无法连接云服务，请稍后重试。" : "无法访问系统凭据，请稍后重试。";
    }
    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); Authentication.ConnectivityChanged -= ConnectivityChanged; Authentication.Dispose(); }
}
