using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using IL.App.Views.Dialogs;

namespace IL.App.Views;

internal sealed class StudentNotice : Border
{
    public StudentNotice(string title, string message, bool error, Action close)
    {
        Height = 96; MaxWidth = 380; CornerRadius = new CornerRadius(6); Padding = new Thickness(14, 12, 8, 12);
        BorderThickness = new Thickness(1);
        this.Bind(BackgroundProperty, new DynamicResourceExtension("CardBackgroundFillColorDefaultBrush"));
        this.Bind(BorderBrushProperty, new DynamicResourceExtension("WorkspaceCardBorderBrush"));
        BoxShadow = new BoxShadows(new BoxShadow { Color = Color.Parse("#1F000000"), Blur = 14, OffsetY = 4 });
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions(error ? "auto,*,auto,auto" : "auto,*,auto") };
        var icon = WorkspaceUi.Icon(error ? "error_badge" : "completed", 19); icon.Foreground = Brush.Parse(error ? "#E81123" : "#107C10"); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 2, 12, 0); content.Children.Add(icon);
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Medium, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = message, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); content.Children.Add(text);
        if (error)
        {
            Button? copy = null;
            copy = WorkspaceUi.IconButton("copy", "复制错误详情", async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync($"{title}\n{message}");
                ToolTip.SetTip(copy!, "已复制");
            });
            copy.Classes.Add("subtle"); copy.Content = WorkspaceUi.Icon("copy", 14); copy.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(copy, 2); content.Children.Add(copy);
        }
        var dismiss = WorkspaceUi.IconButton("chrome_close", "关闭", () => { close(); return Task.CompletedTask; }); dismiss.Content = WorkspaceUi.Icon("chrome_close", 14); dismiss.Classes.Add("subtle"); dismiss.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(dismiss, error ? 3 : 2); content.Children.Add(dismiss); Child = content;
    }
}
