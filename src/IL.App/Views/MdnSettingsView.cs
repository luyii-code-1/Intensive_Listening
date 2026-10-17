using Avalonia;
using Avalonia.Input.Platform;
using IL.Core.Infrastructure;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using IL.App.Views.Dialogs;
using IL.Core.Management;

namespace IL.App.Views;

internal sealed class MdnSettingsView : UserControl
{
    private readonly MdnClient _client;
    private readonly TextBox _address = new() { PlaceholderText = "192.168.1.10:8443", MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _credential = new() { PasswordChar = '●', PlaceholderText = "组织共享注册凭证", MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _http = new() { Content = "使用 HTTP（禁用 SSL，仅用于可信内网）" };
    private readonly TextBlock _status = WorkspaceUi.Text("", 12), _organization = WorkspaceUi.Text("", 14);
    private readonly Button _connect, _retry, _leave, _yaml;
    private readonly StackPanel _enrollment = new() { Spacing = 10 };
    private readonly TextBlock _connection = WorkspaceUi.Text("", 12), _policy = WorkspaceUi.Text("", 12);
    private readonly TextBox _error = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, FontSize = 12 };
    private readonly Expander _errorDetails;
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3, IsVisible = false };
    private string? _operationError;
    private readonly TextBlock _locked = new() { Text = "连接设置由您的组织管理，退出需管理员授权。", FontSize = 12, Foreground = Brushes.Goldenrod, TextWrapping = TextWrapping.Wrap };
    private string? _shownServer;
    private bool _busy;
    public MdnSettingsView(MdnClient client)
    {
        _client = client;
        _connect = WorkspaceUi.Button("连接并加入管理", () => RunAsync(JoinAsync), true);
        _yaml = WorkspaceUi.Button("选择 YAML 加入管理", () => RunAsync(JoinYamlAsync));
        _retry = WorkspaceUi.Button("立即重试连接", () => RunAsync(() => client.SynchronizeAsync()));
        _leave = WorkspaceUi.Button("退出组织管理", () => RunAsync(LeaveAsync));
        var panel = new StackPanel { Spacing = 14, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(WorkspaceUi.Text("组织管理", 22, true));
        panel.Children.Add(WorkspaceUi.Text("安装后可连接组织服务器；首次连接确认服务器身份与传输方式。", 12));
        panel.Children.Add(_organization);
        panel.Children.Add(_connection); panel.Children.Add(_policy);
        _enrollment.Children.Add(WorkspaceUi.Text("服务器 IP 或 HTTP/HTTPS 地址")); _enrollment.Children.Add(_address); _enrollment.Children.Add(_http);
        _enrollment.Children.Add(WorkspaceUi.Text("注册凭证")); _enrollment.Children.Add(_credential); panel.Children.Add(_enrollment);
        panel.Children.Add(_progress); panel.Children.Add(_status);
        var copyError = WorkspaceUi.Button("复制连接诊断", async () => { if (TopLevel.GetTopLevel(this)?.Clipboard is {} clipboard) await clipboard.SetTextAsync(_error.Text); });
        _errorDetails = new Expander { Header = "连接诊断详情", Content = WorkspaceUi.Stack(_error, copyError), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(_errorDetails);
        panel.Children.Add(_locked);
        panel.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { _connect, _yaml, _retry, _leave } });
        panel.Children.Add(WorkspaceUi.Text("退出管理需由管理员在后台授权。连接失败时保留已生效的组织策略。", 12));
        Content = panel;
        client.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _http.IsCheckedChanged += (_, _) =>
        {
            if (_client.Configuration is not null) return;
            if (Uri.TryCreate(_address.Text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                _address.Text = new UriBuilder(uri) { Scheme = _http.IsChecked == true ? "http" : "https", Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri.AbsoluteUri;
        };
        Refresh();
    }
    private void Refresh()
    {
        var managed = _client.Configuration is not null;
        if (managed) WorkspaceUi.OrganizationText(_organization, "当前由", _client.Configuration!.OrganizationName, $"管理 · {_client.DeviceId.Split('-')[^1]}");
        else { _organization.Inlines = null; _organization.Text = "尚未加入组织管理"; }
        if (!_busy) _status.Text = _operationError == null ? _client.Status : "操作未完成，可展开连接诊断详情。";
        _progress.IsVisible = _busy; _enrollment.IsVisible = !managed;
        _connection.IsVisible = _policy.IsVisible = managed;
        _connection.Text = managed ? $"服务器：{_client.Configuration!.ServerUrl}\n连接：{(_client.IsRealtime ? "实时模式 · " + (_client.LatencyMs?.ToString() ?? "--") + " ms" : "轮询模式 · " + _client.Configuration.PollSeconds + " 秒")}\n最近同步：{(_client.LastSynchronizedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未同步")}" : "";
        _policy.Text = $"策略版本：{_client.Policy.Revision} · {(_client.PolicyReceiptConfirmed ? "服务端已确认接收回执" : "等待服务端确认回执")}\n受管设置：{_client.Policy.Settings.Count} 项 · 禁用功能：{_client.Policy.DisabledFeatures.Length} 项 · 开机自启由组织管理";
        _error.Text = _operationError ?? _client.LastConnectionError ?? ""; _errorDetails.IsVisible = !string.IsNullOrWhiteSpace(_error.Text);
        if (managed && _shownServer != _client.Configuration!.ServerUrl) { _shownServer = _client.Configuration.ServerUrl; _http.IsChecked = _client.Configuration.DisableSsl; _address.Text = _shownServer; }
        _address.IsEnabled = _credential.IsEnabled = !_busy && !managed;
        _http.IsEnabled = !_busy && !managed; _locked.IsVisible = managed;
        _connect.IsVisible = !managed; _connect.IsEnabled = !_busy;
        _yaml.IsVisible = !managed; _yaml.IsEnabled = !_busy;
        _retry.IsVisible = _leave.IsVisible = managed;
        _retry.IsEnabled = _leave.IsEnabled = !_busy;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _operationError = null; Refresh(); _status.Text = "正在与组织服务器通信…";
        try { await action(); }
        catch (Exception error)
        {
            _operationError = CrashReportStore.Redact(error.ToString());
            AppLog.Error("组织管理操作失败", error);
        }
        finally { _busy = false; Refresh(); }
    }
    private async Task JoinAsync()
    {
        var server = MdnEnrollment.NormalizeAddress(_address.Text ?? "", _http.IsChecked == true);
        var credential = _credential.Text?.Trim() ?? "";
        if (credential.Length == 0) throw new FormatException("请填写组织共享注册凭证。");
        var identity = await MdnEnrollment.DiscoverAsync(server);
        if (!await ConfirmTransportAsync(server, identity)) return;
        await _client.JoinAsync(server, identity, credential); _credential.Text = "";
    }
    private async Task JoinYamlAsync()
    {
        if (_client.Configuration is not null) throw new InvalidOperationException("已加入组织管理，无法导入注册配置。");
        var path = await WorkspaceUi.Pick(this, "选择组织注册配置", "yaml", "yml");
        if (path is null) return;
        var config = MdnConfiguration.LoadFile(path);
        var identity = new MdnServerInfo(config.OrganizationName, config.UpdatePublicKey, config.ServerCertificateSha256);
        if (await ConfirmTransportAsync(config.Server, identity)) await _client.JoinYamlAsync(path);
    }
    private async Task<bool> ConfirmTransportAsync(Uri server, MdnServerInfo identity)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        var transport = server.Scheme == "http"
            ? "当前使用 HTTP 明文连接。注册凭证、策略、遥测及下发的 API 配置信息在传输中不加密，请仅在可信内网使用。确认后会保存此选择并发送注册凭证。"
            : $"证书 SHA-256：\n{identity.CertificateSha256}\n\n请与管理员提供的指纹核对。确认后会保存信任并发送注册凭证。";
        var organization = WorkspaceUi.Text(""); WorkspaceUi.OrganizationText(organization, "组织：", identity.OrganizationName);
        var dialog = AppDialogs.Create(owner, "确认组织服务器", WorkspaceUi.Stack(organization, WorkspaceUi.Text($"服务器：{server}\n\n{transport}")));
        dialog.PrimaryButtonText = "确认连接方式"; dialog.CloseButtonText = "取消"; dialog.DefaultButton = FAContentDialogButton.Primary;
        return await dialog.ShowAsync(owner) == FAContentDialogResult.Primary;
    }
    private async Task LeaveAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (await AppDialogs.ConfirmAsync(owner, "退出组织管理", "服务器将检查管理员授权。退出后恢复本机个人设置。", "检查授权并退出")) await _client.LeaveAsync();
    }
}
