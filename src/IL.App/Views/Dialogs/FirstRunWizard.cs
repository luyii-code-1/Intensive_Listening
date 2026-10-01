using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using IL.Core.Settings;

namespace IL.App.Views.Dialogs;

internal sealed class FirstRunWizard : Border
{
    private const string Retention = "关闭后会停止后续上报并清理待发送缓存。已送达的数据不会立即撤回；阿里云文档所述默认保留期为 60 天，实际以工作区配置为准。";
    private const string Disclosure = "开启后，应用会向阿里云 ARMS 发送匿名设备 ID、应用版本、启动日期、ASR 模型、API 主机名、耗时与缓存命中状态、系统和硬件信息，以及应用内红色错误提示的原文。SDK 默认自动采集支持的网络请求信息和原生崩溃诊断信息。网络请求信息可能包含 URL 及参数；错误和崩溃信息可能包含文件名、服务响应或调用堆栈；服务还可能记录时间戳和网络 IP。";
    private readonly Window _owner;
    private readonly AppSettings _initial;
    private readonly string _agreement, _privacy;
    private readonly Action<AppSettings?> _finish;
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _fraction = WorkspaceUi.Text("");
    private int _step, _font;
    private bool _association, _mcp, _skip, _telemetry, _accepted, _privateAccepted;
    private string _theme, _key;

    private FirstRunWizard(Window owner, AppSettings initial, string agreement, string privacy, Action<AppSettings?> finish)
    {
        _owner = owner; _initial = initial; _agreement = agreement; _privacy = privacy; _finish = finish;
        _association = initial.EulaAcceptedVersion.Length == 0 || initial.FileAssociationEnabled;
        _mcp = initial.EulaAcceptedVersion.Length == 0 || initial.McpEnabled;
        _skip = initial.SkipOpeningPrompts; _telemetry = !initial.TelemetryPrompted || initial.TelemetryEnabled;
        _font = initial.TranscriptFontSize; _theme = initial.ThemeMode; _key = initial.CloudApiKey;
        CornerRadius = new CornerRadius(12); ClipToBounds = true; MaxWidth = 1040; MaxHeight = 740;
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        var dark = owner.ActualThemeVariant == ThemeVariant.Dark;
        Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.Parse(dark ? "#202936" : "#EDF7FF"), 0), new(Color.Parse(dark ? "#292637" : "#F1F1FF"), 1) } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("24,10,*,Auto"), Margin = new Thickness(22, 14, 14, 0) };
        header.Children.Add(Brand(24)); var name = WorkspaceUi.Text("Intensive Listening"); name.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(name, 2); header.Children.Add(name);
        var close = WorkspaceUi.IconButton("cancel", "关闭", () => { finish(null); return Task.CompletedTask; }); Grid.SetColumn(close, 3); header.Children.Add(close); layout.Children.Add(header);
        var centered = new Grid(); centered.Children.Add(_page);
        var scroll = new ScrollViewer { Content = centered, Padding = new Thickness(28, 24), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _page.MaxWidth = 730; _page.HorizontalAlignment = HorizontalAlignment.Center; _page.VerticalAlignment = VerticalAlignment.Center;
        scroll.SizeChanged += (_, _) => { centered.MinHeight = Math.Max(0, scroll.Bounds.Height - 48); _page.Width = Math.Max(0, Math.Min(730, scroll.Bounds.Width - 56)); };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(22, 0, 22, 16) };
        footer.Children.Add(WorkspaceUi.Text("2.0.0-dev Prelude")); Grid.SetColumn(_fraction, 1); footer.Children.Add(_fraction); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Child = layout; Render();
    }
    internal static async Task<AppSettings?> ShowAsync(Window owner, AppSettings initial, string agreement, string privacy)
    {
        var completion = new TaskCompletionSource<AppSettings?>();
        var root = owner.Content as Grid ?? throw new InvalidOperationException("首次设置需要应用根布局。");
        var layer = new Border { Background = new SolidColorBrush(Color.Parse("#80000000")), Padding = new Thickness(16), Focusable = true };
        var wizard = new FirstRunWizard(owner, initial, agreement, privacy, value => completion.TrySetResult(value));
        layer.Child = wizard;
        void Resize() { wizard.Width = Math.Max(0, Math.Min(owner.ClientSize.Width - 32, 1040)); wizard.Height = Math.Max(0, Math.Min(owner.ClientSize.Height - 32, 740)); }
        EventHandler<SizeChangedEventArgs> resize = (_, _) => Resize();
        owner.SizeChanged += resize;
        layer.KeyDown += (_, e) => { if (e.Key == Key.Escape) { completion.TrySetResult(null); e.Handled = true; } };
        root.Children.Add(layer); Resize(); layer.Focus();
        try { return await completion.Task; } finally { owner.SizeChanged -= resize; root.Children.Remove(layer); }
    }
    private static Border Brand(double size)
    {
        var icon = WorkspaceUi.Icon("play", size * .48); icon.Foreground = Brushes.White;
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * .24), Background = WorkspaceUi.Accent, Child = icon, HorizontalAlignment = HorizontalAlignment.Center };
    }
    private static TextBlock CenterText(string value, double size = 14, bool bold = false)
    { var text = WorkspaceUi.Text(value, size, bold); text.TextAlignment = TextAlignment.Center; text.HorizontalAlignment = HorizontalAlignment.Center; return text; }
    private static StackPanel Page(string title, string description)
    { var body = new StackPanel { Spacing = 8 }; body.Children.Add(CenterText(title, 22, true)); var subtitle = CenterText(description); subtitle.Margin = new Thickness(0, 0, 0, 20); body.Children.Add(subtitle); return body; }
    private Control Card(string icon, string title, string description, Control trailing)
    {
        var card = WorkspaceUi.SettingsRow(icon, title, description, trailing);
        // The wizard uses its own brand color, including card icons.
        if (card is Border { Child: Grid grid } && grid.Children[0] is TextBlock glyph) glyph.Foreground = WorkspaceUi.Accent;
        return card;
    }
    private ToggleSwitch Toggle(bool value, Action<bool> set)
    { var toggle = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "", MinWidth = 40 }; toggle.IsCheckedChanged += (_, _) => set(toggle.IsChecked == true); return toggle; }
    private Control Navigation(bool first = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0) };
        if (!first) { var back = WorkspaceUi.IconButton("chevron_left", "上一步", () => { _step = _initial.CloudReady && _step == 5 ? 3 : _step - 1; Render(); return Task.CompletedTask; }); back.Classes.Add("round"); back.Content = WorkspaceUi.Icon("chevron_left", 22); row.Children.Add(back); }
        var next = WorkspaceUi.IconButton(_step == 5 ? "accept" : "chevron_right", _step == 5 ? "完成设置" : "下一步", () => { Next(); return Task.CompletedTask; }, true);
        next.Classes.Add("round"); next.Background = WorkspaceUi.Accent; next.Content = WorkspaceUi.Icon(_step == 5 ? "accept" : "chevron_right", 22);
        next.IsEnabled = _step != 1 || (_accepted && _privateAccepted); row.Children.Add(next); return row;
    }
    private void Next()
    {
        if (_step == 1 && (!_accepted || !_privateAccepted)) return;
        if (_step == 5) { _finish(_initial with { EulaAcceptedVersion = "2026-09-22", FileAssociationEnabled = _association, FileAssociationPrompted = true, McpEnabled = _mcp, SkipOpeningPrompts = _skip, TelemetryEnabled = _telemetry, TelemetryPrompted = true, TranscriptFontSize = _font, ThemeMode = _theme, CloudApiKey = _key.Trim() }); return; }
        _step = _initial.CloudReady && _step == 3 ? 5 : _step + 1; Render();
    }
    private void Render()
    {
        StackPanel body;
        switch (_step)
        {
            case 0:
                body = new StackPanel { Spacing = 8 };
                var brand = Brand(76); brand.Margin = new Thickness(0, 0, 0, 18); body.Children.Add(brand);
                body.Children.Add(CenterText("Intensive Listening", 22, true)); body.Children.Add(CenterText("欢迎使用精听课程制作与播放")); break;
            case 1:
                body = Page("同意许可条款", "请先阅读并确认用户协议与隐私说明。");
                body.Children.Add(Card("document", "用户协议", "了解软件许可、用户内容和云端服务的使用说明。", WorkspaceUi.Button("阅读", () => AppDialogs.DocumentAsync(_owner, "用户协议", _agreement))));
                body.Children.Add(Card("info", "隐私说明", "了解本机保存的数据和匿名数据分析的范围。", WorkspaceUi.Button("阅读", () => AppDialogs.DocumentAsync(_owner, "隐私说明", _privacy))));
                var accepted = new CheckBox { Content = "我已阅读并同意用户协议", IsChecked = _accepted, Margin = new Thickness(0, 10, 0, 0) };
                var privateAccepted = new CheckBox { Content = "我已阅读并同意隐私说明", IsChecked = _privateAccepted };
                accepted.IsCheckedChanged += (_, _) => { _accepted = accepted.IsChecked == true; UpdateGate(); };
                privateAccepted.IsCheckedChanged += (_, _) => { _privateAccepted = privateAccepted.IsChecked == true; UpdateGate(); };
                body.Children.Add(accepted); body.Children.Add(privateAccepted); break;
            case 2:
                body = Page("基本设置", "选择文件打开方式、制作接口和播放习惯。");
                body.Children.Add(Card("open_file", "关联 .ilp 文件", "双击精听包即可在应用中打开。", Toggle(_association, value => _association = value)));
                body.Children.Add(Card("robot", "MCP 制作接口", "允许本机智能体连接；接管仍需在应用内批准。", Toggle(_mcp, value => _mcp = value)));
                body.Children.Add(Card("forward", "跳过题前提示", "首次打开课程时直接定位到第一题前并暂停。", Toggle(_skip, value => _skip = value)));
                var telemetry = new ToggleSwitch { IsChecked = _telemetry, OnContent = "", OffContent = "", MinWidth = 40 };
                var settingTelemetry = false;
                telemetry.IsCheckedChanged += async (_, _) => { if (settingTelemetry) return; var enabled = telemetry.IsChecked == true; if (!enabled && _telemetry && !await AppDialogs.ConfirmAsync(_owner, "关闭匿名数据分析？", Retention + "\n\n关闭后可以随时在设置中重新开启。", "确认关闭")) { settingTelemetry = true; telemetry.IsChecked = true; settingTelemetry = false; return; } _telemetry = enabled; };
                body.Children.Add(Card("chart", "匿名数据分析", "帮助了解应用使用情况与错误，可在设置中更改。", telemetry));
                var disclosure = WorkspaceUi.Text(Disclosure + "\n" + Retention, 12); disclosure.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(disclosure); break;
            case 3:
                body = Page("外观", "调整播放时的阅读体验。");
                var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = _theme == "light" ? 1 : _theme == "dark" ? 2 : 0, MinWidth = 100 };
                theme.SelectionChanged += (_, _) => _theme = new[] { "system", "light", "dark" }[Math.Max(0, theme.SelectedIndex)];
                body.Children.Add(Card("color", "外观主题", "按系统设置或选择浅色、深色模式。", theme));
                var font = new Slider { Minimum = 14, Maximum = 28, TickFrequency = 1, IsSnapToTickEnabled = true, Value = _font, Width = 200 };
                var value = WorkspaceUi.Text(_font.ToString()); value.VerticalAlignment = VerticalAlignment.Center;
                font.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { _font = (int)Math.Round(font.Value); value.Text = _font.ToString(); } };
                body.Children.Add(Card("font", "字幕字体大小", "调整播放页逐句字幕的字号。", WorkspaceUi.Row(font, value))); break;
            case 4:
                body = Page("云端转写", "填写 API Key 后即可制作课程，也可以稍后再配置。");
                var key = new TextBox { Text = _key, PasswordChar = '●', PlaceholderText = "可留空", Width = 270 };
                key.TextChanged += (_, _) => _key = key.Text ?? "";
                body.Children.Add(Card("cloud", "API Key", "密钥只保存在本机设置中。", key));
                var help = WorkspaceUi.Button("如何配置？", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://il.luyii.cn/guide.html#api-setup") { UseShellExecute = true }); return Task.CompletedTask; }); help.HorizontalAlignment = HorizontalAlignment.Center; help.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(help); break;
            default:
                body = new StackPanel { Spacing = 10 }; var doneBrand = Brand(66); doneBrand.Margin = new Thickness(0, 0, 0, 14); body.Children.Add(doneBrand); body.Children.Add(CenterText("设置完成", 22, true)); body.Children.Add(CenterText("现在可以开始播放精听包，或创建一份课程。")); break;
        }
        body.Children.Add(Navigation(_step == 0));
        _page.Content = body; _fraction.Text = $"{(_initial.CloudReady && _step == 5 ? 5 : _step + 1)} / {(_initial.CloudReady ? 5 : 6)}";
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(260), Easing = new CubicEaseOut(), Children = { new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } }, new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } } } };
        _ = animation.RunAsync(body);
    }
    private void UpdateGate()
    { if (_page.Content is StackPanel { Children: var children } && children.LastOrDefault() is StackPanel nav && nav.Children.LastOrDefault() is Button next) next.IsEnabled = _accepted && _privateAccepted; }
}
