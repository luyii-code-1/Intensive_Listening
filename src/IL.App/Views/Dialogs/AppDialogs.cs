using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;
using IL.Core.Settings;
using IL.Core.Mcp;

namespace IL.App.Views.Dialogs;

public static class AppDialogs
{
    public static async Task<int> ChooseAsync(Window owner, string title, string message, params string[] choices)
    {
        var answer = -1;
        var body = WorkspaceUi.Stack(WorkspaceUi.Text(message));
        var dialog = new FAContentDialog { Title = title, Content = body, MaxWidth = 560 };
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            var button = WorkspaceUi.Button(choices[i], () => { answer = index; dialog.Hide(); return Task.CompletedTask; }, i == choices.Length - 1);
            button.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(button);
        }
        body.Children.Add(row); await dialog.ShowAsync(owner); return answer;
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
        var dialog = new FAContentDialog { Title = title, Content = WorkspaceUi.Stack(WorkspaceUi.Text("请填写用于加密或解密 API 配置的密码。"), input), CloseButtonText = "取消", PrimaryButtonText = "确认", DefaultButton = FAContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = string.IsNullOrEmpty(input.Text);
        return await dialog.ShowAsync(owner) == FAContentDialogResult.Primary ? input.Text : null;
    }
    public static async Task DocumentAsync(Window owner, string title, string text)
    {
        var dialog = new FAContentDialog { Title = title, MaxWidth = 700, MaxHeight = 660, CloseButtonText = "关闭",
            Content = new ScrollViewer { Width = 620, Height = 460, Content = new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } } };
        await dialog.ShowAsync(owner);
    }
    public static Task<AppSettings?> FirstRunAsync(Window owner, AppSettings initial, string agreement, string privacy) =>
        FirstRunWizard.ShowAsync(owner, initial, agreement, privacy);
}
