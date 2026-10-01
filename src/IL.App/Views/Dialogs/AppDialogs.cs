using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;
using IL.Core.Settings;
using IL.Core.Mcp;

namespace IL.App.Views.Dialogs;

public static class AppDialogs
{
    public static FAContentDialog Create(Window owner, string title, object content, double maxWidth = 560, double maxHeight = 756)
    {
        var dialog = new SpringContentDialog { Title = title, Content = content };
        // The control includes the smoke layer; only the inner surface is constrained.
        void Resize()
        {
            dialog.Resources["ContentDialogMaxWidth"] = Math.Max(0, Math.Min(maxWidth, owner.ClientSize.Width - 32));
            dialog.Resources["ContentDialogMaxHeight"] = Math.Max(0, Math.Min(maxHeight, owner.ClientSize.Height - 32));
        }
        void OnSizeChanged(object? sender, SizeChangedEventArgs args) => Resize();
        Resize(); owner.SizeChanged += OnSizeChanged;
        dialog.Closed += (_, _) => owner.SizeChanged -= OnSizeChanged;
        return dialog;
    }
    public static async Task<int> ChooseAsync(Window owner, string title, string message, params string[] choices)
    {
        var answer = -1;
        var dialog = Create(owner, title, WorkspaceUi.Text(message));
        // ContentDialog owns the footer so keyboard actions and pointer actions share one result.
        if (choices.Length <= 3)
        {
            if (choices.Length > 0) { dialog.PrimaryButtonText = choices[^1]; dialog.PrimaryButtonClick += (_, _) => answer = choices.Length - 1; dialog.DefaultButton = FAContentDialogButton.Primary; }
            if (choices.Length > 1) { dialog.CloseButtonText = choices[0]; dialog.CloseButtonClick += (_, _) => answer = 0; }
            if (choices.Length == 3) { dialog.SecondaryButtonText = choices[1]; dialog.SecondaryButtonClick += (_, _) => answer = 1; }
        }
        else throw new ArgumentOutOfRangeException(nameof(choices), "对话框最多支持三个操作。");
        await dialog.ShowAsync(owner); return answer;
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
        var dialog = Create(owner, title, WorkspaceUi.Stack(WorkspaceUi.Text("请填写用于加密或解密 API 配置的密码。"), input));
        dialog.CloseButtonText = "取消"; dialog.PrimaryButtonText = "确认"; dialog.DefaultButton = FAContentDialogButton.Primary;
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = string.IsNullOrEmpty(input.Text);
        return await dialog.ShowAsync(owner) == FAContentDialogResult.Primary ? input.Text : null;
    }
    public static async Task DocumentAsync(Window owner, string title, string text)
    {
        var dialog = Create(owner, title, new ScrollViewer { MaxWidth = 620, Height = 460, Content = new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } }, 700, 660);
        dialog.CloseButtonText = "关闭";
        await dialog.ShowAsync(owner);
    }
    public static Task<AppSettings?> FirstRunAsync(Window owner, AppSettings initial, string agreement, string privacy) =>
        FirstRunWizard.ShowAsync(owner, initial, agreement, privacy);
}
