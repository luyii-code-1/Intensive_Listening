using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using IL.Core.Settings;
using IL.Core.Mcp;

namespace IL.App.Views.Dialogs;

public static class AppDialogs
{
    public static async Task<int> ChooseAsync(Window owner, string title, string message, params string[] choices)
    {
        var body = WorkspaceUi.Stack(WorkspaceUi.Text(message));
        var window = Create(owner, title, body);
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            row.Children.Add(WorkspaceUi.Button(choices[i], () => { window.Close(index); return Task.CompletedTask; }, i == 0));
        }
        body.Children.Add(row);
        window.Closed += (_, _) => { };
        return await window.ShowDialog<int>(owner);
    }
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string accept = "确认") =>
        await ChooseAsync(owner, title, message, "取消", accept) == 1;
    public static async Task<AgentApprovalDecision> ApproveAgentAsync(Window owner, string name)
    {
        var choice = await ChooseAsync(owner, "智能体请求接管", $"「{name}」请求读取与修改本机课程工程，并执行课程制作操作。批准后编辑区交由该智能体使用，可随时结束接管。", "拒绝", "批准接管", "关闭 MCP 接口");
        return choice switch { 1 => AgentApprovalDecision.Approve, 2 => AgentApprovalDecision.DisableMcp, _ => AgentApprovalDecision.Refuse };
    }
    public static Task<bool> ConfirmForcedCutsAsync(Window owner) => ConfirmAsync(owner, "音频需要强制分段", "部分音频缺少足够的静音间隔，需要在最长分段位置切开。分段边界可能影响识别，请确认继续。", "继续转写");
    public static async Task<string?> PasswordAsync(Window owner, string title)
    {
        var input = new TextBox { PasswordChar = '●', PlaceholderText = "配置归档密码", MinWidth = 330 };
        var body = WorkspaceUi.Stack(WorkspaceUi.Text("请填写用于加密或解密 API 配置的密码。"), input);
        var window = Create(owner, title, body);
        body.Children.Add(WorkspaceUi.Row(WorkspaceUi.Button("取消", () => { window.Close(); return Task.CompletedTask; }), WorkspaceUi.Button("确认", () => { if (!string.IsNullOrEmpty(input.Text)) window.Close(input.Text); return Task.CompletedTask; }, true)));
        return await window.ShowDialog<string?>(owner);
    }
    public static async Task DocumentAsync(Window owner, string title, string text)
    {
        var body = WorkspaceUi.Stack(new ScrollViewer { Content = new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, MaxHeight = 480 });
        var window = Create(owner, title, body, 720);
        body.Children.Add(WorkspaceUi.Button("关闭", () => { window.Close(); return Task.CompletedTask; }, true));
        await window.ShowDialog(owner);
    }
    public static async Task<AppSettings?> FirstRunAsync(Window owner, AppSettings initial, string agreement, string privacy)
    {
        var accepted = new CheckBox { Content = "我已阅读并同意用户协议", IsChecked = false };
        var privacyAccepted = new CheckBox { Content = "我已阅读并同意隐私说明", IsChecked = false };
        var association = new CheckBox { Content = "关联 .ilp 文件", IsChecked = true };
        var mcp = new CheckBox { Content = "启用本机 MCP 制作接口（接管需批准）", IsChecked = true };
        var telemetry = new CheckBox { Content = "允许匿名数据分析", IsChecked = initial.TelemetryPrompted ? initial.TelemetryEnabled : true };
        var skip = new CheckBox { Content = "打开课程时跳过题前提示", IsChecked = initial.SkipOpeningPrompts };
        var key = new TextBox { PasswordChar = '●', Text = initial.CloudApiKey, PlaceholderText = "API Key（可稍后配置）" };
        var theme = new ComboBox { ItemsSource = new[] { "system", "light", "dark" }, SelectedItem = initial.ThemeMode, MinWidth = 160 };
        var font = new NumericUpDown { Minimum = 14, Maximum = 28, Value = initial.TranscriptFontSize, Width = 160 };
        var body = WorkspaceUi.Stack(WorkspaceUi.Text("欢迎使用 Intensive Listening", 22, true), WorkspaceUi.Text("精听课程制作与播放"),
            WorkspaceUi.Row(WorkspaceUi.Button("阅读用户协议", () => DocumentAsync(owner, "用户协议", agreement)), WorkspaceUi.Button("阅读隐私说明", () => DocumentAsync(owner, "隐私说明", privacy))),
            accepted, privacyAccepted, new Separator(), association, mcp, skip, telemetry,
            WorkspaceUi.Text("匿名分析用于了解应用使用情况与错误。音频、字幕、试卷、API 密钥和课程内容保存在本机，不作为匿名分析内容上传。", 12),
            WorkspaceUi.Row(WorkspaceUi.Field("外观", theme), WorkspaceUi.Field("字幕字号", font)), WorkspaceUi.Field("云端转写 API Key", key));
        var window = Create(owner, "首次设置", body, 620);
        var finish = WorkspaceUi.Button("完成设置", () =>
        {
            window.Close(initial with { EulaAcceptedVersion = "2026-09-22", FileAssociationEnabled = association.IsChecked == true,
                FileAssociationPrompted = true, McpEnabled = mcp.IsChecked == true, TelemetryEnabled = telemetry.IsChecked == true,
                TelemetryPrompted = true, TranscriptFontSize = (int)(font.Value ?? 18), SkipOpeningPrompts = skip.IsChecked == true, CloudApiKey = key.Text?.Trim() ?? "", ThemeMode = theme.SelectedItem as string ?? "system" });
            return Task.CompletedTask;
        }, true);
        finish.IsEnabled = false;
        accepted.IsCheckedChanged += (_, _) => finish.IsEnabled = accepted.IsChecked == true && privacyAccepted.IsChecked == true;
        privacyAccepted.IsCheckedChanged += (_, _) => finish.IsEnabled = accepted.IsChecked == true && privacyAccepted.IsChecked == true;
        body.Children.Add(WorkspaceUi.Row(WorkspaceUi.Button("退出", () => { window.Close(); return Task.CompletedTask; }), finish));
        return await window.ShowDialog<AppSettings?>(owner);
    }
    private static Window Create(Window owner, string title, Control body, double width = 540) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 760, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new Border { Padding = new Thickness(24), Child = body }
    };
}
