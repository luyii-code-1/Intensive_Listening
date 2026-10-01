using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using IL.App.Views.Dialogs;

namespace IL.App.Views;

internal static class SettingsPasswordDialog
{
    public static async Task<string?> ShowAsync(Window owner, bool confirm)
    {
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "密码" };
        var repeated = new TextBox { PasswordChar = '●', PlaceholderText = "再次输入密码", IsVisible = confirm };
        var error = WorkspaceUi.Text(""); error.Foreground = Brushes.Firebrick; error.IsVisible = false;
        var content = WorkspaceUi.Stack(password, repeated, error); content.Width = 360;
        var dialog = AppDialogs.Create(owner, confirm ? "设置配置包密码" : "输入配置包密码", content);
        dialog.PrimaryButtonText = confirm ? "导出" : "导入"; dialog.CloseButtonText = "取消";
        dialog.DefaultButton = FAContentDialogButton.Primary;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var message = string.IsNullOrEmpty(password.Text) ? "密码不能为空" : confirm && password.Text != repeated.Text ? "两次输入的密码不一致" : "";
            if (message.Length == 0) return;
            error.Text = message; error.IsVisible = true; args.Cancel = true;
        };
        dialog.Opened += (_, _) => password.Focus();
        return await dialog.ShowAsync(owner) == FAContentDialogResult.Primary ? password.Text : null;
    }
}
