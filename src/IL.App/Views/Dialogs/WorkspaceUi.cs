using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;

namespace IL.App.Views.Dialogs;

internal static class WorkspaceUi
{
    public static readonly FontFamily BodyFont = new("avares://IL.App/Assets/Fonts#HarmonyOS Sans SC");
    private static readonly FontFamily IconFont = new("avares://IL.App/Assets/Fonts#Fabric MDL2 Assets");
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#B80018"));
    public static TextBlock Icon(string name, double size = 16) => new()
    {
        Text = char.ConvertFromUtf32(DartIcons.Glyphs.TryGetValue(name, out var code) ? code : DartIcons.Glyphs["info"]),
        FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    public static Button IconButton(string name, string tooltip, Func<Task> action, bool primary = false)
    {
        var button = Button("", action, primary); button.Content = Icon(name);
        button.Padding = new Thickness(8); button.MinWidth = 32; button.MinHeight = 32;
        ToolTip.SetTip(button, tooltip); return button;
    }
    public static Control PageHeader(string title, Control? commands = null, Func<Task>? back = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(24, 22, 24, 20), MinHeight = 36 };
        if (back is not null) { var button = IconButton("back", "返回", back); button.Classes.Add("subtle"); button.Margin = new Thickness(-12, 0, 8, 0); grid.Children.Add(button); }
        var text = Text(title, 22, true); text.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (commands is not null) { Grid.SetColumn(commands, 2); commands.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(commands); }
        return grid;
    }
    public static Control SettingsRow(string icon, string title, string description, Control trailing)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = 44 };
        var glyph = Icon(icon, 22); glyph.Margin = new Thickness(0, 0, 18, 0); grid.Children.Add(glyph);
        var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Text(title, 14, true)); labels.Children.Add(Text(description, 12)); Grid.SetColumn(labels, 1); grid.Children.Add(labels);
        trailing.Margin = new Thickness(16, 0, 0, 0); trailing.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(trailing, 2); grid.Children.Add(trailing);
        var border = Surface(grid, new Thickness(20, 16)); border.CornerRadius = new CornerRadius(4);
        border.BorderThickness = new Thickness(1); border.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("WorkspaceCardBorderBrush")); return border;
    }
    public static TextBlock Text(string text, double size = 14, bool strong = false) => new()
    { Text = text, FontSize = size, FontWeight = strong ? (size >= 22 ? FontWeight.Bold : FontWeight.Medium) : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
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
