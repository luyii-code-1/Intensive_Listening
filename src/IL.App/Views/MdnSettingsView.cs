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
    private readonly TextBlock _status = WorkspaceUi.Text("", 12), _organization = WorkspaceUi.Text("", 14);
    private readonly Button _connect, _retry, _leave;
    private bool _busy;
    public MdnSettingsView(MdnClient client)
    {
        _client = client;
        _connect = WorkspaceUi.Button("连接并加入管理", () => RunAsync(JoinAsync), true);
        _retry = WorkspaceUi.Button("立即重试连接", () => RunAsync(() => client.SynchronizeAsync()));
        _leave = WorkspaceUi.Button("退出组织管理", () => RunAsync(LeaveAsync));
        var panel = new StackPanel { Spacing = 14, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(WorkspaceUi.Text("组织管理", 22, true));
        panel.Children.Add(WorkspaceUi.Text("安装后可连接组织服务器；首次连接需要确认服务器证书指纹。", 12));
        panel.Children.Add(_organization);
        panel.Children.Add(WorkspaceUi.Text("服务器 IP 或 HTTPS 地址")); panel.Children.Add(_address);
        panel.Children.Add(WorkspaceUi.Text("注册凭证")); panel.Children.Add(_credential);
        panel.Children.Add(_status);
        panel.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { _connect, _retry, _leave } });
        panel.Children.Add(WorkspaceUi.Text("退出管理需由管理员在后台授权。连接失败时保留已生效的组织策略。", 12));
        Content = panel;
        client.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }
    private void Refresh()
    {
        var managed = _client.Configuration is not null;
        _organization.Text = managed ? _client.Banner : "尚未加入组织管理";
        _status.Text = _client.Status;
        if (managed) _address.Text = _client.Configuration!.ServerUrl;
        _address.IsEnabled = _credential.IsEnabled = !_busy && !managed;
        _connect.IsVisible = !managed; _connect.IsEnabled = !_busy;
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
        var server = MdnEnrollment.NormalizeAddress(_address.Text ?? "");
        var credential = _credential.Text?.Trim() ?? "";
        if (credential.Length == 0) throw new FormatException("请填写组织共享注册凭证。");
        var identity = await MdnEnrollment.DiscoverAsync(server);
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await AppDialogs.ConfirmAsync(owner, "确认组织服务器",
            $"组织：{identity.OrganizationName}\n服务器：{server}\n证书 SHA-256：\n{identity.CertificateSha256}\n\n请与管理员提供的指纹核对。确认后会保存信任并发送注册凭证。", "信任并加入")) return;
        await _client.JoinAsync(server, identity, credential); _credential.Text = "";
    }
    private async Task LeaveAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (await AppDialogs.ConfirmAsync(owner, "退出组织管理", "服务器将检查管理员授权。退出后恢复本机个人设置。", "检查授权并退出")) await _client.LeaveAsync();
    }
}
