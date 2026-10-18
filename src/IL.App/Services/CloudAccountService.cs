using System.Diagnostics;
using IL.Core.Cloud;
using IL.Core.Infrastructure;

namespace IL.App.Services;

public sealed class CloudAccountService : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _login;
    private int _busy;
    private bool _disposed;
    public CloudAuthClient Authentication { get; } = new(new CloudCredentialStore());
    public event Action? Changed;
    public string Status { get; private set; } = "未登录 Intensive Listening 云服务";
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool Enabled { get; private set; }
    public bool IsSignedIn => Enabled && Authentication.HasCredentials;
    public async Task ApplyEnabledAsync(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        if (!enabled) { CancelLogin(); Status = "云服务已关闭"; Notify(); return; }
        Status = "未登录 Intensive Listening 云服务"; Notify();
        await RestoreAsync();
    }

    public async Task RestoreAsync()
    {
        if (!Enabled || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            await Authentication.RestoreAsync(_lifetime.Token);
            if (Authentication.HasCredentials)
            {
                Status = "正在确认 Intensive Listening 云服务 登录状态…"; Notify();
                await Authentication.GetAccessTokenAsync(_lifetime.Token);
                Status = "已登录 Intensive Listening 云服务";
            }
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) Status = "连接 Intensive Listening 云服务 超时，请稍后重试。"; }
        catch (CloudAuthenticationException error) { Status = error.Message; }
        catch (HttpRequestException) { Status = "暂时无法连接 Intensive Listening 云服务，请稍后重试。"; }
        catch (Exception) { Status = "无法读取 Intensive Listening 云服务 登录状态，请检查系统凭据访问权限。"; }
        finally { Interlocked.Exchange(ref _busy, 0); Notify(); }
    }

    public async Task LoginAsync()
    {
        if (!Enabled || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        var deviceName = Environment.MachineName;
        using var login = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _login = login;
        try
        {
            Status = "正在连接 Intensive Listening 云服务 登录服务…"; Notify();
            await Authentication.EnsureAuthorizationAvailableAsync(login.Token);
            await using var callback = new CloudLoopbackCallback();
            Status = "请在浏览器中登录并允许授权…"; Notify();
            Process.Start(new ProcessStartInfo(callback.Request.BrowserUri(deviceName).AbsoluteUri) { UseShellExecute = true });
            var result = await callback.WaitAsync(login.Token);
            Status = "正在完成登录…"; Notify();
            await Authentication.CompleteLoginAsync(callback.Request, result, login.Token);
            Status = "已登录 Intensive Listening 云服务";
            AppLog.Operation("cloud.auth", "Intensive Listening 云服务 浏览器授权登录完成");
        }
        catch (OperationCanceledException) { Status = login.IsCancellationRequested ? "已取消登录。" : "浏览器授权等待超时，请重试。"; }
        catch (CloudAuthenticationException error) { Status = error.Message; }
        catch (HttpRequestException) { Status = "无法连接 Intensive Listening 云服务，请检查网络后重试。"; }
        catch (Exception) { Status = "无法完成 Intensive Listening 云服务 登录，请检查系统浏览器与凭据访问权限。"; }
        finally { _login = null; Interlocked.Exchange(ref _busy, 0); Notify(); }
    }

    public void CancelLogin() => _login?.Cancel();

    public void AuthenticationFailed(string message)
    {
        Status = message; Notify();
    }

    public async Task SignOutAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            Status = "正在退出 Intensive Listening 云服务…"; Notify();
            await Authentication.SignOutAsync(_lifetime.Token);
            Status = "已退出 Intensive Listening 云服务";
            AppLog.Operation("cloud.auth", "Intensive Listening 云服务 账号已退出");
        }
        catch (CloudAuthenticationException error) { Status = error.Message; }
        catch (HttpRequestException) { Status = "已退出本机账号，暂时无法撤销云端设备，请在 Intensive Listening 云服务 中检查登录设备。"; }
        catch (OperationCanceledException) { Status = "退出操作已取消，请检查登录状态。"; }
        catch (Exception) { Status = "退出操作未完成，请检查系统凭据访问权限。"; }
        finally { Interlocked.Exchange(ref _busy, 0); Notify(); }
    }

    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        Authentication.Dispose();
    }
}
