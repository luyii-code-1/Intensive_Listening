using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using IL.App.Services;

namespace IL.App.Views.Dialogs;

internal sealed class CloudOnboardingView : StackPanel
{
    private readonly CloudAccountService _account;
    private readonly ToggleSwitch _enabled;
    private readonly TextBlock _status = WorkspaceUi.Text("", 12);
    private readonly Button _login, _cancel;

    public CloudOnboardingView(CloudAccountService account, bool enabled, Action<bool> changed)
    {
        _account = account; Spacing = 12;
        _enabled = new ToggleSwitch { IsChecked = enabled };
        _login = WorkspaceUi.Button("浏览器登录", account.LoginAsync, true);
        _cancel = WorkspaceUi.Button("取消登录", () => { account.CancelLogin(); return Task.CompletedTask; });
        Children.Add(ManagedUi.Setting(WorkspaceUi.SettingsRow("cloud", "启用云服务", "使用账号连接云端课程与转写接口", _enabled), "cloudServiceEnabled"));
        Children.Add(_status);
        var actions = WorkspaceUi.Row(_login, _cancel); actions.HorizontalAlignment = HorizontalAlignment.Center; Children.Add(actions);
        _enabled.IsCheckedChanged += async (_, _) =>
        {
            var selected = _enabled.IsChecked == true; changed(selected);
            await account.ApplyEnabledAsync(selected); Refresh();
        };
        AttachedToVisualTree += (_, _) => { account.Changed += Changed; Refresh(); };
        DetachedFromVisualTree += (_, _) => account.Changed -= Changed;
        Refresh();
    }
    private void Changed() => Dispatcher.UIThread.Post(Refresh);
    private void Refresh()
    {
        _enabled.IsEnabled = !_account.IsBusy;
        _status.Text = _enabled.IsChecked == true ? _account.Status : "可稍后在设置中开启。";
        _login.IsVisible = _enabled.IsChecked == true;
        _login.Content = _account.IsSignedIn ? "重新授权" : "浏览器登录";
        _login.IsEnabled = _account.CanAuthorize;
        _cancel.IsVisible = _account.IsBusy;
    }
}
