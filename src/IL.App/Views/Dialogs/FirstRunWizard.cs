using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Windowing;
using System.Runtime.InteropServices;
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
    private readonly Grid _page = new() { ClipToBounds = true, Name = "OobePageHost" };
    private readonly Dictionary<int, StackPanel> _pages = new();
    private readonly bool _windows;
    private CancellationTokenSource? _transition;
    private Button? _licenseNext;
    private WelcomeIntro? _intro;
    private int _visibleStep = -1;
    private bool _attached, _introduced;
    internal static Func<bool>? ReducedMotionOverride { get; set; }
    private readonly TextBlock _fraction = WorkspaceUi.Text("");
    private int _step, _font;
    private bool _association, _mcp, _skip, _telemetry, _accepted, _privateAccepted;
    private string _theme, _key;

    private FirstRunWizard(Window owner, AppSettings initial, string agreement, string privacy, Action<AppSettings?> finish, bool windows = false)
    {
        _owner = owner; _initial = initial; _agreement = agreement; _privacy = privacy; _finish = finish; _windows = windows;
        _association = initial.EulaAcceptedVersion.Length == 0 || initial.FileAssociationEnabled;
        _mcp = initial.EulaAcceptedVersion.Length == 0 || initial.McpEnabled;
        _skip = initial.SkipOpeningPrompts; _telemetry = !initial.TelemetryPrompted || initial.TelemetryEnabled;
        _font = initial.TranscriptFontSize; _theme = initial.ThemeMode; _key = initial.CloudApiKey;
        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;
        var layout = new Grid { RowDefinitions = new RowDefinitions(windows ? "*,Auto" : "Auto,*,Auto") };
        if (!windows)
        {
            CornerRadius = new CornerRadius(12); MaxWidth = 1040; MaxHeight = 740;
            HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
            var dark = owner.ActualThemeVariant == ThemeVariant.Dark;
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.Parse(dark ? "#202936" : "#EDF7FF"), 0), new(Color.Parse(dark ? "#292637" : "#F1F1FF"), 1) } };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("24,10,*,Auto"), Margin = new Thickness(22, 14, 14, 0) };
            header.Children.Add(Brand(24)); var name = WorkspaceUi.Text("Intensive Listening"); name.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(name, 2); header.Children.Add(name);
            var close = WorkspaceUi.IconButton("cancel", "关闭", () => { finish(null); return Task.CompletedTask; }); Grid.SetColumn(close, 3); header.Children.Add(close); layout.Children.Add(header);
        }
        var centered = new Grid(); centered.Children.Add(_page);
        var scroll = new ScrollViewer { Content = centered, Padding = new Thickness(windows ? 40 : 28, 20), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _page.MaxWidth = windows ? 680 : 730; _page.HorizontalAlignment = HorizontalAlignment.Center; _page.VerticalAlignment = VerticalAlignment.Center;
        scroll.SizeChanged += (_, _) => { centered.MinHeight = Math.Max(0, scroll.Bounds.Height - 40); _page.Width = Math.Max(0, Math.Min(_page.MaxWidth, scroll.Bounds.Width - (windows ? 80 : 56))); };
        Grid.SetRow(scroll, windows ? 0 : 1); layout.Children.Add(scroll);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(22, 0, 22, windows ? 12 : 16) };
        var version = CenterText("2.0.0-dev Prelude", 12); version.Opacity = .75; Grid.SetColumn(version, 1); footer.Children.Add(version);
        _fraction.FontSize = 12; _fraction.Opacity = .6; _fraction.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(_fraction, 2); footer.Children.Add(_fraction); Grid.SetRow(footer, windows ? 1 : 2); layout.Children.Add(footer);
        Child = layout; Render();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            if (!MotionReduced() && _pages.TryGetValue(_step, out var attachedPage))
            {
                attachedPage.Opacity = 0; attachedPage.RenderTransform = new TranslateTransform(0, 24);
            }
            Dispatcher.UIThread.Post(() => { if (_attached) _ = AnimateAttachedPageAsync(); }, DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree += (_, _) => { _attached = false; CancelTransition(); };
    }
    internal static Task<AppSettings?> ShowAsync(Window owner, AppSettings initial, string agreement, string privacy) =>
        OperatingSystem.IsWindows() ? ShowWindowsAsync(owner, initial, agreement, privacy) : ShowInlineAsync(owner, initial, agreement, privacy);

    internal static Window CreateWindowsWindow(Window owner, AppSettings initial, string agreement, string privacy)
    {
        var window = new FAAppWindow
        {
            Title = "Intensive Listening", Width = 800, Height = 600, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowAsDialog = true,
            RequestedThemeVariant = owner.ActualThemeVariant, Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None],
            TransparencyBackgroundFallback = new SolidColorBrush(Color.Parse(owner.ActualThemeVariant == ThemeVariant.Dark ? "#202020" : "#F3F3F3")),
        };
        var root = new Grid();
        root.Children.Add(new FirstRunWizard(window, initial, agreement, privacy, value => window.Close(value), windows: true));
        window.Content = root;
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
        return window;
    }
    internal static async Task<AppSettings?> ShowWindowsAsync(Window owner, AppSettings initial, string agreement, string privacy) =>
        await CreateWindowsWindow(owner, initial, agreement, privacy).ShowDialog<AppSettings?>(owner);

    private static async Task<AppSettings?> ShowInlineAsync(Window owner, AppSettings initial, string agreement, string privacy)
    {
        var completion = new TaskCompletionSource<AppSettings?>();
        var root = owner.Content as Grid ?? throw new InvalidOperationException("首次设置需要应用根布局。");
        var layer = new Border { Background = new SolidColorBrush(Color.Parse("#80000000")), Padding = new Thickness(16), Focusable = true };
        var wizard = new FirstRunWizard(owner, initial, agreement, privacy, value => completion.TrySetResult(value));
        layer.Child = wizard;
        void Resize() { wizard.Width = Math.Max(0, Math.Min(owner.ClientSize.Width - 32, 1040)); wizard.Height = Math.Max(0, Math.Min(owner.ClientSize.Height - 32, 740)); }
        EventHandler<SizeChangedEventArgs> resize = (_, _) => Resize();
        EventHandler closed = (_, _) => completion.TrySetResult(null);
        owner.SizeChanged += resize; owner.Closed += closed;
        layer.KeyDown += (_, e) => { if (e.Key == Key.Escape) { completion.TrySetResult(null); e.Handled = true; } };
        root.Children.Add(layer); Resize(); layer.Focus();
        try { return await completion.Task; } finally { owner.SizeChanged -= resize; owner.Closed -= closed; root.Children.Remove(layer); }
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
        if (_windows && card is Border surface) surface.Padding = new Thickness(16, 10);
        if (card is Border { Child: Grid grid } && grid.Children[0] is TextBlock glyph) glyph.Foreground = WorkspaceUi.Accent;
        return card;
    }
    private ToggleSwitch Toggle(bool value, Action<bool> set)
    { var toggle = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "", MinWidth = 40 }; toggle.IsCheckedChanged += (_, _) => set(toggle.IsChecked == true); return toggle; }
    private Control Navigation(bool first = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, _windows ? 12 : 16, 0, 0) };
        Button RoundButton(string icon, string tooltip, bool accent)
        {
            var button = new Button { Content = WorkspaceUi.Icon(icon, 18), Width = 48, Height = 48, CornerRadius = new CornerRadius(24), Padding = new Thickness(0) };
            if (accent) button.Classes.Add("accent");
            ToolTip.SetTip(button, tooltip); return button;
        }
        if (!first)
        {
            var back = RoundButton("chevron_left", "上一步", false); back.Name = "OobeBack";
            back.Click += (_, _) => { _step = _initial.CloudReady && _step == 5 ? 3 : _step - 1; Render(); };
            row.Children.Add(back);
        }
        var next = RoundButton(_step == 5 ? "accept" : "chevron_right", _step == 5 ? "完成设置" : "下一步", true);
        next.Name = "OobeNext"; next.Click += (_, _) => Next();
        next.IsEnabled = _step != 1 || (_accepted && _privateAccepted);
        if (_step == 1) _licenseNext = next;
        row.Children.Add(next); return row;
    }
    private void Next()
    {
        if (_step == 1 && (!_accepted || !_privateAccepted)) return;
        if (_step == 5) { _finish(_initial with { EulaAcceptedVersion = "2026-09-22", FileAssociationEnabled = _association, FileAssociationPrompted = true, McpEnabled = _mcp, SkipOpeningPrompts = _skip, TelemetryEnabled = _telemetry, TelemetryPrompted = true, TranscriptFontSize = _font, ThemeMode = _theme, CloudApiKey = _key.Trim() }); return; }
        _step = _initial.CloudReady && _step == 3 ? 5 : _step + 1; Render();
    }
    private StackPanel BuildPage()
    {
        StackPanel body;
        switch (_step)
        {
            case 0:
                body = new StackPanel { Spacing = 8 };
                var brand = Brand(_windows ? 80 : 76); brand.Margin = new Thickness(0, 0, 0, 8); body.Children.Add(brand);
                var brandTitle = CenterText("Intensive Listening", _windows ? 32 : 22, true);
                if (_windows) brandTitle.FontWeight = FontWeight.Medium;
                body.Children.Add(brandTitle); body.Children.Add(CenterText("欢迎使用精听课程制作与播放")); break;
            case 1:
                body = Page("同意许可条款", "请先阅读并确认用户协议与隐私说明。");
                CheckBox Consent(string document, string text, bool value, Action<bool> set)
                {
                    var link = new Button { Content = $"《{document}》", Padding = new Thickness(2, 0), MinHeight = 24, Foreground = WorkspaceUi.Accent };
                    link.Classes.Add("subtle"); link.Click += async (_, e) => { e.Handled = true; await AppDialogs.DocumentAsync(_owner, document, text); };
                    var wording = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    wording.Children.Add(WorkspaceUi.Text("我已阅读并同意")); wording.Children.Add(link);
                    var check = new CheckBox { Content = wording, IsChecked = value, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                    check.IsCheckedChanged += (_, _) => { set(check.IsChecked == true); UpdateGate(); };
                    return check;
                }
                body.Children.Add(Consent("用户协议", _agreement, _accepted, value => _accepted = value));
                body.Children.Add(Consent("隐私说明", _privacy, _privateAccepted, value => _privateAccepted = value)); break;
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
        if (_windows) body.Spacing = 6;
        body.Name = $"OobePage{_step}";
        body.Children.Add(Navigation(_step == 0));
        return body;
    }
    private void Render()
    {
        var previous = _visibleStep;
        CancelTransition();
        if (!_pages.TryGetValue(_step, out var body)) _pages[_step] = body = BuildPage();
        _visibleStep = _step;
        _fraction.Text = $"{(_initial.CloudReady && _step == 5 ? 5 : _step + 1)} / {(_initial.CloudReady ? 5 : 6)}";
        _fraction.IsVisible = _step != 0;
        if (!_attached || MotionReduced())
        {
            _page.Children.Clear(); _page.Children.Add(body); Normalize(body); return;
        }
        _ = TransitionAsync(body, previous < _step ? 1 : -1);
    }
    private async Task AnimateAttachedPageAsync()
    {
        CancelTransition();
        var body = _pages[_step];
        if (MotionReduced()) { Normalize(body); return; }
        var cancellation = _transition = new CancellationTokenSource();
        try
        {
            if (_windows && _step == 0 && !_introduced)
            {
                _introduced = true;
                body.Opacity = 0;
                _intro = new WelcomeIntro(); _page.Children.Add(_intro);
                await _intro.PlayAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _page.Children.Remove(_intro); _intro = null;
            }
            await Entrance(body, 0, 24, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        finally { if (_transition == cancellation) Normalize(body); }
    }
    private async Task TransitionAsync(StackPanel incoming, int direction)
    {
        var outgoing = _page.Children.OfType<StackPanel>().FirstOrDefault();
        var cancellation = _transition = new CancellationTokenSource();
        if (outgoing == incoming) { Normalize(incoming); return; }
        incoming.Opacity = 0; incoming.RenderTransform = new TranslateTransform(direction * 80, 0);
        _page.Children.Add(incoming);
        try
        {
            var incomingTask = Entrance(incoming, direction * 80, 0, cancellation.Token);
            if (outgoing is not null)
            {
                outgoing.IsHitTestVisible = false;
                await Task.WhenAll(incomingTask, Slide(outgoing, 0, -direction * 80, 1, 0, cancellation.Token));
            }
            else await incomingTask;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_transition == cancellation)
            {
                _page.Children.Clear(); _page.Children.Add(incoming); Normalize(incoming);
            }
        }
    }
    private static Task Entrance(Control body, double x, double y, CancellationToken cancellation)
    {
        body.Opacity = 0; body.RenderTransform = new TranslateTransform(x, y);
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(300), Easing = new CubicEaseOut(), FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.XProperty, x), new Setter(TranslateTransform.YProperty, y) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.XProperty, 0d), new Setter(TranslateTransform.YProperty, 0d) } },
            }
        };
        return animation.RunAsync(body, cancellationToken: cancellation);
    }
    private static Task Slide(Control body, double from, double to, double opacityFrom, double opacityTo, CancellationToken cancellation)
    {
        body.RenderTransform = new TranslateTransform(from, 0);
        return new Animation
        {
            Duration = TimeSpan.FromMilliseconds(300), Easing = new CubicEaseOut(), FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, opacityFrom), new Setter(TranslateTransform.XProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, opacityTo), new Setter(TranslateTransform.XProperty, to) } },
            }
        }.RunAsync(body, cancellationToken: cancellation);
    }
    private static void Normalize(Control body) { body.Opacity = 1; body.RenderTransform = new TranslateTransform(); body.IsHitTestVisible = true; }
    private void CancelTransition()
    {
        _transition?.Cancel(); _transition?.Dispose(); _transition = null;
        if (_intro is not null) { _page.Children.Remove(_intro); _intro = null; }
        var current = _visibleStep >= 0 && _pages.TryGetValue(_visibleStep, out var body) ? body : null;
        _page.Children.Clear();
        if (current is not null) { Normalize(current); _page.Children.Add(current); }
    }
    private void UpdateGate()
    { if (_licenseNext is not null) _licenseNext.IsEnabled = _accepted && _privateAccepted; }

    internal static bool MotionReduced()
    {
        if (ReducedMotionOverride is { } preference) return preference();
        if (OperatingSystem.IsWindows()) return SystemParametersInfo(0x1042, 0, out var enabled, 0) && !enabled;
        if (OperatingSystem.IsMacOS())
        {
            var key = CFStringCreateWithCString(IntPtr.Zero, "reduceMotion", 0x08000100);
            var domain = CFStringCreateWithCString(IntPtr.Zero, "com.apple.universalaccess", 0x08000100);
            var value = CFPreferencesCopyAppValue(key, domain);
            try { return value != IntPtr.Zero && CFGetTypeID(value) == CFBooleanGetTypeID() && CFBooleanGetValue(value); }
            finally { if (value != IntPtr.Zero) CFRelease(value); CFRelease(key); CFRelease(domain); }
        }
        return false;
    }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern IntPtr CFPreferencesCopyAppValue(IntPtr key, IntPtr applicationId);
    [DllImport(CoreFoundation)] private static extern ulong CFGetTypeID(IntPtr value);
    [DllImport(CoreFoundation)] private static extern ulong CFBooleanGetTypeID();
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr value);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
}
