using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;

namespace IL.App.Views.Dialogs;

internal static class WorkspaceUi
{
    public static readonly FontFamily BodyFont = new("avares://IL.App/Assets/Fonts#HarmonyOS Sans SC");
    private static readonly FontFamily IconFont = new("avares://IL.App/Assets/Fonts#Fabric MDL2 Assets");
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#B80018"));
    public static TextBlock Icon(string name, double size = 16) => new()
    {
        Text = char.ConvertFromUtf32(DartIcons.Glyphs.TryGetValue(name, out var code) ? code : DartIcons.Glyphs["info"]),
        FontFamily = IconFont, FontWeight = FontWeight.Normal, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
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
    // Use the same Fluent settings control for individual rows and expandable groups.
    public static FAIconSource SettingsIcon(string icon) => new FAFontIconSource
    {
        Glyph = char.ConvertFromUtf32(DartIcons.Glyphs.TryGetValue(icon, out var code) ? code : DartIcons.Glyphs["info"]),
        FontFamily = IconFont, FontSize = 20
    };
    public static Control SettingsRow(string icon, string title, string description, Control trailing, bool compact = false) =>
        SettingsGroup(icon, title, description, null, trailing);
    public static Control SettingsRow(string icon, string title, Control description, Control trailing, bool compact = false)
    {
        var row = SettingsGroup(icon, title, "", null, trailing);
        row.Header = new StackPanel { Spacing = 2, Children = { Text(title), description } };
        return row;
    }
    public static FASettingsExpander SettingsGroup(string icon, string title, string description, Control? content, Control? action = null, bool expanded = false)
    {
        var row = new FASettingsExpander
        {
            IconSource = SettingsIcon(icon), Header = title, Description = description,
            Footer = action, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0), MinHeight = 64
        };
        if (action is not null) { action.Margin = new Thickness(12, 0, 0, 0); action.VerticalAlignment = VerticalAlignment.Center; }
        if (content is not null) row.Items.Add(content);
        return row;
    }
    public static TextBlock Text(string text, double size = 14, bool strong = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (strong) block.FontWeight = FontWeight.Bold;
        return block;
    }
    public static void OrganizationText(TextBlock label, string prefix, string organization, string suffix = "")
    {
        label.Text = null;
        label.Inlines = new InlineCollection { new Run(prefix), new Run(organization) { FontWeight = FontWeight.Bold }, new Run(suffix) };
    }
    public static StackPanel Stack(params Control[] controls) { var panel = new StackPanel { Spacing = 8 }; foreach (var c in controls) panel.Children.Add(c); return panel; }
    public static WrapPanel Row(params Control[] controls) { var panel = new WrapPanel { Orientation = Orientation.Horizontal }; foreach (var c in controls) { c.Margin = new Thickness(0, 0, 8, 6); panel.Children.Add(c); } return panel; }
    public static Button Button(string title, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = title };
        if (primary) button.Classes.Add("accent");
        button.Click += async (_, _) =>
        {
            Control origin = TopLevel.GetTopLevel(button) ?? (Control)button;
            var label = string.IsNullOrEmpty(title) ? ToolTip.GetTip(button)?.ToString() ?? action.Method.Name : title;
            var operation = Guid.NewGuid().ToString("N")[..8];
            IL.Core.Infrastructure.AppLog.Operation("ui", $"操作开始 id={operation} button={label}");
            button.IsEnabled = false;
            try
            {
                if (await IL.App.Services.UiActionRunner.RunAsync(action) is { } error)
                {
                    var managed = error is IL.Core.Management.ManagedFeatureDeniedException;
                    if (!managed) IL.Core.Infrastructure.AppLog.Warning($"界面操作未完成 id={operation} button={label}", error);
                    IL.App.Views.WorkspaceToast.Show(origin, managed ? "组织管理限制" : "操作未完成", error.Message, true);
                }
                else IL.Core.Infrastructure.AppLog.Operation("ui", $"操作完成 id={operation} button={label}");
            }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    public static Button NextButton(string title, Func<Task> action)
    {
        var button = Button(title, action, true);
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Text(title), new RoundedArrowGlyph() } };
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
        var patterns = extensions.Select(e => "*." + e).ToArray();
        var supported = patterns.Length == 0 ? null : new FilePickerFileType($"{title} ({string.Join(";", patterns)})") { Patterns = patterns };
        var files = await Owner(control).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false,
            FileTypeFilter = supported is null ? null : [supported], SuggestedFileType = supported });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    public static async Task<string?> Save(Control control, string title, string filename, string extension)
    {
        var file = await Owner(control).StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = filename,
            DefaultExtension = extension, ShowOverwritePrompt = true, FileTypeChoices = [new FilePickerFileType(title) { Patterns = ["*." + extension] }] });
        return file?.TryGetLocalPath();
    }
}
