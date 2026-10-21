using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using IL.App.Services;
using IL.App.Views.Dialogs;
using IL.Core.Cloud;

namespace IL.App.Views;

public sealed class CloudSettingsView : UserControl
{
    public const string ServiceName = "Intensive Listening 云服务";
    private readonly ToggleSwitch _enabled = new();
    private readonly TextBlock _status = WorkspaceUi.Text("云服务已关闭", 13);
    private readonly Button _login, _cancel, _logout;
    private readonly AppServices _services;
    private bool _refreshing;

    public CloudSettingsView(AppServices services, Action? settingsChanged = null)
    {
        _services = services;
        _login = WorkspaceUi.Button("浏览器登录", services.CloudAccount.LoginAsync, true);
        _cancel = WorkspaceUi.Button("取消登录", () => { services.CloudAccount.CancelLogin(); return Task.CompletedTask; });
        _logout = WorkspaceUi.Button("退出登录", services.CloudAccount.SignOutAsync);
        var web = WorkspaceUi.Button("打开云服务", () =>
        {
            Process.Start(new ProcessStartInfo(CloudAuthClient.Server.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        _enabled.IsCheckedChanged += async (_, _) =>
        {
            if (_refreshing) return;
            _enabled.IsEnabled = false;
            try
            {
                var settings = services.Settings with { CloudServiceEnabled = _enabled.IsChecked == true };
                await services.SettingsStore.SaveAsync(settings);
                await services.ApplySettingsAsync(settings);
                if (_enabled.IsChecked == true && !services.CloudAccount.IsSignedIn) await services.CloudAccount.LoginAsync();
            }
            catch (Exception error) { WorkspaceToast.Show(this, "云服务设置未保存", error.Message, true); }
            finally { settingsChanged?.Invoke(); Refresh(); }
        };
        Content = new StackPanel
        {
            MaxWidth = 920, Spacing = 20, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                WorkspaceUi.Text(ServiceName, 20, true),
                ManagedUi.Setting(WorkspaceUi.SettingsRow("cloud", "启用云服务", "登录后使用云端课程和云端转写", _enabled, compact: true), "cloudServiceEnabled"),
                _status, WorkspaceUi.Row(_login, _cancel, _logout, web)
            }
        };
        AttachedToVisualTree += (_, _) => { services.CloudAccount.Changed += Changed; Refresh(); };
        DetachedFromVisualTree += (_, _) => services.CloudAccount.Changed -= Changed;
        Refresh();
    }
    private void Changed() => Dispatcher.UIThread.Post(Refresh);
    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var account = _services.CloudAccount;
            _enabled.IsChecked = _services.Settings.CloudServiceEnabled;
            _enabled.IsEnabled = !account.IsBusy;
            _status.Text = account.Enabled ? account.Status : "云服务已关闭";
            _login.IsVisible = account.Enabled;
            _login.Content = account.IsSignedIn ? "重新授权" : "浏览器登录";
            _login.IsEnabled = account.CanAuthorize;
            _cancel.IsVisible = account.IsBusy;
            _logout.IsVisible = account.IsSignedIn;
            _logout.IsEnabled = account.CanSignOut;
        }
        finally { _refreshing = false; }
    }
}
