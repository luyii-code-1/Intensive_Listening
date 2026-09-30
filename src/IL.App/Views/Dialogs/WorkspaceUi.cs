using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;

namespace IL.App.Views.Dialogs;

internal static class WorkspaceUi
{
    public static TextBlock Text(string text, double size = 14, bool strong = false) => new()
    { Text = text, FontSize = size, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    public static StackPanel Stack(params Control[] controls) { var panel = new StackPanel { Spacing = 8 }; foreach (var c in controls) panel.Children.Add(c); return panel; }
    public static WrapPanel Row(params Control[] controls) { var panel = new WrapPanel { Orientation = Orientation.Horizontal }; foreach (var c in controls) { c.Margin = new Thickness(0, 0, 8, 6); panel.Children.Add(c); } return panel; }
    public static Button Button(string title, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = title };
        if (primary) button.Classes.Add("accent");
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } finally { button.IsEnabled = true; } };
        return button;
    }
    public static Control Field(string label, Control input) => Stack(Text(label, 12, true), input);
    public static TextBox Input(string? value = null, string? hint = null, bool multiline = false) => new()
    { Text = value, PlaceholderText = hint, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 80 : 0 };
    public static Border Surface(Control child, Thickness? padding = null)
    {
        var border = new Border { Child = child, Padding = padding ?? new Thickness(14), CornerRadius = new CornerRadius(6) };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("CardBackgroundFillColorDefaultBrush"));
        return border;
    }
    public static Window Owner(Control control) => TopLevel.GetTopLevel(control) as Window ?? throw new InvalidOperationException("窗口尚未打开。");
    public static async Task<string?> Pick(Control control, string title, params string[] extensions)
    {
        var files = await Owner(control).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false,
            FileTypeFilter = extensions.Length == 0 ? null : [new FilePickerFileType(title) { Patterns = extensions.Select(e => "*." + e).ToArray() }] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    public static async Task<string?> Save(Control control, string title, string filename, string extension)
    {
        var file = await Owner(control).StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = filename,
            DefaultExtension = extension, ShowOverwritePrompt = true, FileTypeChoices = [new FilePickerFileType(title) { Patterns = ["*." + extension] }] });
        return file?.TryGetLocalPath();
    }
}
