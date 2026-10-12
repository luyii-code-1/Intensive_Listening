using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
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
    private readonly Button _connect, _retry, _leave, _transport, _yaml;
    private string? _shownServer;
    private bool _busy;
    public MdnSettingsView(MdnClient client)
    {
        _client = client;
        _connect = WorkspaceUi.Button("连接并加入管理", () => RunAsync(JoinAsync), true);
        _yaml = WorkspaceUi.Button("选择 YAML 加入管理", () => RunAsync(JoinYamlAsync));
        _retry = WorkspaceUi.Button("立即重试连接", () => RunAsync(() => client.SynchronizeAsync()));
        _leave = WorkspaceUi.Button("退出组织管理", () => RunAsync(LeaveAsync));
        _transport = WorkspaceUi.Button("应用连接方式并重连", () => RunAsync(ChangeTransportAsync));
        var panel = new StackPanel { Spacing = 14, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(WorkspaceUi.Text("组织管理", 22, true));
        panel.Children.Add(WorkspaceUi.Text("安装后可连接组织服务器；首次连接确认服务器身份与传输方式。", 12));
        panel.Children.Add(_organization);
        panel.Children.Add(WorkspaceUi.Text("服务器 IP 或 HTTP/HTTPS 地址")); panel.Children.Add(_address); panel.Children.Add(_http);
        panel.Children.Add(WorkspaceUi.Text("注册凭证")); panel.Children.Add(_credential);
        panel.Children.Add(_status);
        panel.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { _connect, _yaml, _transport, _retry, _leave } });
        panel.Children.Add(WorkspaceUi.Text("退出管理需由管理员在后台授权。连接失败时保留已生效的组织策略。", 12));
        Content = panel;
        client.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _http.IsCheckedChanged += (_, _) =>
        {
            if (Uri.TryCreate(_address.Text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                _address.Text = new UriBuilder(uri) { Scheme = _http.IsChecked == true ? "http" : "https", Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri.AbsoluteUri;
        };
        Refresh();
    }
    private void Refresh()
    {
        var managed = _client.Configuration is not null;
        _organization.Text = managed ? _client.Banner : "尚未加入组织管理";
        _status.Text = _client.Status;
        if (managed && _shownServer != _client.Configuration!.ServerUrl) { _shownServer = _client.Configuration.ServerUrl; _http.IsChecked = _client.Configuration.DisableSsl; _address.Text = _shownServer; }
        _address.IsEnabled = _credential.IsEnabled = !_busy && !managed;
        _http.IsEnabled = !_busy; _transport.IsVisible = managed; _transport.IsEnabled = !_busy;
        _connect.IsVisible = !managed; _connect.IsEnabled = !_busy;
        _yaml.IsVisible = !managed; _yaml.IsEnabled = !_busy;
        _retry.IsVisible = _leave.IsVisible = managed;
        _retry.IsEnabled = _leave.IsEnabled = !_busy;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; Refresh(); _status.Text = "正在连接服务器…";
        try { await action(); Refresh(); }
        catch (Exception error) { _status.Text = error is InvalidOperationException or FormatException ? error.Message : "连接失败，请检查服务器地址、注册凭证和网络后重试。"; }
        finally { _busy = false; var message = _status.Text; Refresh(); _status.Text = message; }
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
        return await AppDialogs.ConfirmAsync(owner, "确认组织服务器", $"组织：{identity.OrganizationName}\n服务器：{server}\n\n{transport}", "确认连接方式");
    }
    private async Task ChangeTransportAsync()
    {
        var config = _client.Configuration ?? throw new InvalidOperationException("请先加入组织管理。");
        var disabled = _http.IsChecked == true;
        var server = MdnEnrollment.NormalizeAddress(_address.Text ?? "", disabled);
        var identity = disabled ? new MdnServerInfo(config.OrganizationName, config.UpdatePublicKey, "") : await MdnEnrollment.DiscoverAsync(server);
        if (identity.UpdatePublicKey != config.UpdatePublicKey) throw new InvalidOperationException("服务器签名公钥发生变化，请联系管理员确认组织身份。");
        if (await ConfirmTransportAsync(server, identity)) await _client.ChangeTransportAsync(disabled, identity.CertificateSha256);
    }
    private async Task LeaveAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (await AppDialogs.ConfirmAsync(owner, "退出组织管理", "服务器将检查管理员授权。退出后恢复本机个人设置。", "检查授权并退出")) await _client.LeaveAsync();
    }
}
