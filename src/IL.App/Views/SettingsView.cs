using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using IL.App.Views.Dialogs;
using IL.App.Services;
using IL.Core.Infrastructure;
using IL.Core.Settings;
using IL.Core.Mcp;

namespace IL.App.Views;

public sealed class SettingsView : UserControl
{
    private readonly AppSettingsStore _store;
    private readonly AppPrivateApiServer? _mcpServer;
    private readonly StackPanel _agents = new() { Spacing = 8 };
    private readonly Func<AppSettings, Task> _apply;
    private readonly PlayerShortcutSettingsView _playerShortcuts;
    private AppSettings _settings = AppSettings.Defaults();
    private Task _preferenceSaves = Task.CompletedTask;
    private bool _populating;
    private readonly TextBox _baseUrl = WorkspaceUi.Input(), _endpoint = WorkspaceUi.Input(), _model = WorkspaceUi.Input(), _key = new() { PasswordChar = '●', PlaceholderText = "输入云端转写密钥" };
    private readonly NumericUpDown _timeout = new() { Minimum = 30, Maximum = 1800, Increment = 30 }, _concurrency = new() { Minimum = 1, Maximum = 10 };
    private readonly Slider _font = new() { Minimum = 14, Maximum = 28, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 200 };
    private readonly TextBlock _fontValue = WorkspaceUi.Text("18");
    private readonly ComboBox _theme = new() { MinWidth = 145 };
    private sealed record FontChoice(string Value, string Label, FontFamily Family);
    private sealed record WeightChoice(FontWeight Weight, string Label);
    private readonly ComboBox _applicationFont = new() { Width = 260, MaxDropDownHeight = 360 };
    private readonly ComboBox _applicationWeight = new() { Width = 180 };
    private readonly TextBox _fontPreview = new()
    {
        Text = "精听，让每一句都听得清楚\nThe quick brown fox jumps over a lazy dog\n1234567890  08:00–12:00",
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 17, MinHeight = 110
    };
    private readonly ToggleSwitch _association = new(), _mcp = new(), _skip = new(), _debug = new(), _telemetry = new();
    private readonly TextBox _localDirectory = WorkspaceUi.Input();
    private readonly ComboBox _localModel = new() { MinWidth = 180 };
    private readonly DispatcherTimer _fontSave = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public Action? BackRequested { get; set; }
    public Func<Task>? WithdrawAgreementRequested { get; set; }
    public SettingsView(AppSettingsStore store, Func<AppSettings, Task>? apply = null, AppPrivateApiServer? mcpServer = null)
    {
        _store = store; _mcpServer = mcpServer; _apply = apply ?? (_ => Task.CompletedTask);
        _playerShortcuts = new(bindings => ChangePreferenceAsync(s => s with { PlayerKeyBindings = bindings }, "扩展按键"));
        _theme.ItemsSource = new[] { ThemeItem("跟随系统", "system"), ThemeItem("浅色模式", "light"), ThemeItem("深色模式", "dark") };
        _applicationFont.ItemsSource = new[] { new FontChoice("", "HarmonyOS Sans SC（默认）", WorkspaceUi.BodyFont) }
            .Concat(FontManager.Current.SystemFonts.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new FontChoice(f.Name, f.Name, f))).ToArray();
        _applicationFont.ItemTemplate = new FuncDataTemplate<FontChoice>((item, _) => item is null ? null : new TextBlock
        {
            Text = item.Label, FontFamily = item.Family, FontWeight = FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240, VerticalAlignment = VerticalAlignment.Center
        });
        _applicationWeight.ItemsSource = new[]
        {
            new WeightChoice(FontWeight.Thin, "纤细 · Thin"), new WeightChoice(FontWeight.ExtraLight, "特细 · Extra Light"),
            new WeightChoice(FontWeight.Light, "细体 · Light"), new WeightChoice(FontWeight.Normal, "常规 · Regular"),
            new WeightChoice(FontWeight.Medium, "中等 · Medium"), new WeightChoice(FontWeight.SemiBold, "半粗 · Semi Bold"),
            new WeightChoice(FontWeight.Bold, "粗体 · Bold"), new WeightChoice(FontWeight.ExtraBold, "特粗 · Extra Bold"),
            new WeightChoice(FontWeight.Black, "黑体 · Black"), new WeightChoice(FontWeight.ExtraBlack, "特黑 · Extra Black")
        };
        _applicationWeight.ItemTemplate = new FuncDataTemplate<WeightChoice>((item, _) => item is null ? null : new TextBlock { Text = item.Label, FontWeight = item.Weight, VerticalAlignment = VerticalAlignment.Center });
        Build(); WirePreferences();
        AttachedToVisualTree += async (_, _) =>
        {
            if (_mcpServer is not null) { _mcpServer.AgentsChanged += OnAgentsChanged; _mcpServer.AgentStateChanged += OnAgentStateChanged; }
            await RunAsync(ReloadAsync);
        };
        DetachedFromVisualTree += (_, _) => { if (_mcpServer is not null) { _mcpServer.AgentsChanged -= OnAgentsChanged; _mcpServer.AgentStateChanged -= OnAgentStateChanged; } _fontSave.Stop(); if (!_populating && (int)Math.Round(_font.Value) != _settings.TranscriptFontSize) _ = ChangePreferenceAsync(s => s with { TranscriptFontSize = (int)Math.Round(_font.Value) }, "字幕字体大小"); };
    }
    public async Task ReloadAsync() { await _preferenceSaves; Populate(await _store.LoadAsync()); await RefreshAgentsAsync(); }
    public void Populate(AppSettings settings)
    {
        _populating = true;
        try
        {
            _settings = settings;
            _baseUrl.Text = settings.CloudBaseUrl; _endpoint.Text = settings.CloudEndpoint; _model.Text = settings.CloudModel; _key.Text = settings.CloudApiKey;
            _timeout.Value = settings.CloudTimeoutSeconds; _concurrency.Value = settings.CloudConcurrency;
            _font.Value = settings.TranscriptFontSize; _fontValue.Text = settings.TranscriptFontSize.ToString();
            _theme.SelectedItem = _theme.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == settings.ThemeMode);
            PopulateTypography(settings);
            _playerShortcuts.Populate(settings.PlayerKeyBindings);
            _association.IsChecked = settings.FileAssociationEnabled; _mcp.IsChecked = settings.McpEnabled; _skip.IsChecked = settings.SkipOpeningPrompts;
            _debug.IsChecked = settings.DebugLogging; _telemetry.IsChecked = settings.TelemetryEnabled;
            _localDirectory.Text = settings.LocalModelsDirectory; _localModel.ItemsSource = settings.DetectedLocalModels; _localModel.SelectedItem = settings.SelectedLocalModel;
        }
        finally { _populating = false; }
    }
    private static ComboBoxItem ThemeItem(string label, string value) => new()
    {
        Content = new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
        Tag = value, VerticalContentAlignment = VerticalAlignment.Center
    };
    private void WirePreferences()
    {
        _theme.SelectionChanged += async (_, _) => { if (!_populating && _theme.SelectedItem is ComboBoxItem { Tag: string mode }) await ChangePreferenceAsync(s => s with { ThemeMode = mode }, "外观主题"); };
        _applicationFont.SelectionChanged += async (_, _) =>
        {
            if (!_populating && _applicationFont.SelectedItem is FontChoice font)
            { await ChangePreferenceAsync(s => s with { ApplicationFontFamily = font.Value }, "应用字体"); UpdateFontPreview(); }
        };
        _applicationWeight.SelectionChanged += async (_, _) =>
        {
            if (!_populating && _applicationWeight.SelectedItem is WeightChoice weight)
            { await ChangePreferenceAsync(s => s with { ApplicationFontWeight = (int)weight.Weight }, "应用字重"); UpdateFontPreview(); }
        };
        _association.IsCheckedChanged += async (_, _) => { if (!_populating) await ChangePreferenceAsync(s => s with { FileAssociationEnabled = _association.IsChecked == true, FileAssociationPrompted = true }, "关联文件格式"); };
        _mcp.IsCheckedChanged += async (_, _) => { if (!_populating) await ChangePreferenceAsync(s => s with { McpEnabled = _mcp.IsChecked == true }, "MCP"); };
        _skip.IsCheckedChanged += async (_, _) => { if (!_populating) await ChangePreferenceAsync(s => s with { SkipOpeningPrompts = _skip.IsChecked == true }, "跳过题前提示"); };
        _debug.IsCheckedChanged += async (_, _) => { if (!_populating) await ChangePreferenceAsync(s => s with { DebugLogging = _debug.IsChecked == true }, "调试模式"); };
        _telemetry.IsCheckedChanged += async (_, _) =>
        {
            if (_populating) return;
            var enabled = _telemetry.IsChecked == true;
            if (!enabled && _settings.TelemetryEnabled && !await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "关闭匿名数据分析？", "关闭后会停止后续上报并清理待发送缓存。已送达的数据不会立即撤回；阿里云文档所述默认保留期为 60 天，实际以工作区配置为准。\n\n关闭后可以随时在设置中重新开启。", "确认关闭"))
            { _populating = true; _telemetry.IsChecked = true; _populating = false; return; }
            await ChangePreferenceAsync(s => s with { TelemetryEnabled = enabled, TelemetryPrompted = true }, "匿名使用情况分析");
        };
        _font.ValueChanged += (_, _) => { _fontValue.Text = ((int)Math.Round(_font.Value)).ToString(); if (!_populating) { _fontSave.Stop(); _fontSave.Start(); } };
        _fontSave.Tick += async (_, _) => { _fontSave.Stop(); await ChangePreferenceAsync(s => s with { TranscriptFontSize = (int)Math.Round(_font.Value) }, "字幕字体大小"); };
    }
    private void PopulateTypography(AppSettings settings)
    {
        _applicationFont.SelectedItem = _applicationFont.Items.OfType<FontChoice>().FirstOrDefault(f => f.Value == settings.ApplicationFontFamily) ?? _applicationFont.Items[0];
        _applicationWeight.SelectedItem = _applicationWeight.Items.OfType<WeightChoice>().FirstOrDefault(w => (int)w.Weight == settings.ApplicationFontWeight);
        UpdateFontPreview();
    }
    private void UpdateFontPreview()
    {
        _fontPreview.FontFamily = ApplicationTypography.ResolveFont(_settings);
        _fontPreview.FontWeight = (FontWeight)_settings.ApplicationFontWeight;
    }
    private async Task ResetTypographyAsync()
    {
        await ChangePreferenceAsync(s => s with { ApplicationFontFamily = "", ApplicationFontWeight = 500 }, "应用字体");
        _populating = true;
        try { PopulateTypography(_settings); } finally { _populating = false; }
    }
    private Task ChangePreferenceAsync(Func<AppSettings, AppSettings> update, string label)
    {
        var next = update(_settings); if (next == _settings) return _preferenceSaves;
        _settings = next;
        var previous = _preferenceSaves;
        _preferenceSaves = PersistPreferenceAsync(previous, next, label);
        return _preferenceSaves;
    }
    private async Task PersistPreferenceAsync(Task previous, AppSettings next, string label)
    {
        await previous;
        try { await PersistAsync(next); WorkspaceToast.Show(this, $"{label}已更新。"); }
        catch (Exception ex) { ShowError("设置未应用", ex); }
    }
    private async Task PersistAsync(AppSettings settings)
    {
        await _apply(settings); await _store.SaveAsync(settings);
        if (Application.Current != null) Application.Current.RequestedThemeVariant = settings.ThemeMode switch { "dark" => ThemeVariant.Dark, "light" => ThemeVariant.Light, _ => ThemeVariant.Default };
    }
    private sealed record SettingsCategory(string Title, string Icon, Control Content);
    private static Control CategoryPage(string title, string description, params Control[] settings)
    {
        var page = new StackPanel { MaxWidth = 1040, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(new StackPanel { Spacing = 4, Children = { WorkspaceUi.Text(title, 20, true), WorkspaceUi.Text(description, 12) } });
        foreach (var setting in settings) page.Children.Add(setting);
        return page;
    }
    private void Build()
    {
        var typography = new StackPanel { Spacing = 10, Margin = new Thickness(18, 8, 18, 10) };
        var fontSelectors = new WrapPanel { Orientation = Orientation.Horizontal };
        var familyField = WorkspaceUi.Field("字体", _applicationFont); familyField.Margin = new Thickness(0, 0, 12, 8); fontSelectors.Children.Add(familyField);
        var weightField = WorkspaceUi.Field("字重", _applicationWeight); weightField.Margin = new Thickness(0, 0, 0, 8); fontSelectors.Children.Add(weightField);
        typography.Children.Add(fontSelectors);
        typography.Children.Add(WorkspaceUi.Field("字体预览 · 可以编辑测试文字", _fontPreview));
        var fontSettings = new Expander
        {
            Header = Row("font", "应用字体与样式", "字体和字重即时应用，标题保留强调层级", ActionButton("恢复默认", ResetTypographyAsync)),
            Content = typography, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var appearance = CategoryPage("外观与字体", "调整应用外观与全局文字显示",
            Card("color", "外观主题", "选择跟随系统、浅色或深色外观", _theme), fontSettings);

        var transcriptSize = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        _fontValue.VerticalAlignment = VerticalAlignment.Center; transcriptSize.Children.Add(_font); transcriptSize.Children.Add(_fontValue);
        var shortcutSettings = new Expander
        {
            Header = Row("play", "扩展按键", "使用翻页笔或键盘单键控制播放，仅在应用前台的播放页生效", null),
            Content = _playerShortcuts, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var playback = CategoryPage("播放", "设置播放习惯、字幕大小与精听包关联",
            Card("forward", "跳过题前提示", "首次打开课程时定位到第一题前并暂停，保留已有播放进度", _skip),
            Card("font", "字幕字体大小", "调整播放页逐句列表的文字大小", transcriptSize),
            shortcutSettings, Card("open_file", "关联文件格式", "双击 .ilp 精听包直接进入播放界面", _association));

        var mcpBody = new StackPanel { Spacing = 10, Margin = new Thickness(18, 8, 18, 10) };
        mcpBody.Children.Add(Actions(ActionButton("复制 MCP 配置", CopyMcpConfigurationAsync), ActionButton("复制操作提示", CopyAgentPromptAsync)));
        mcpBody.Children.Add(WorkspaceUi.Text("智能体授权", 14, true)); mcpBody.Children.Add(_agents);
        mcpBody.Children.Add(new Separator());
        mcpBody.Children.Add(WorkspaceUi.Text("使用指南", 14, true));
        mcpBody.Children.Add(WorkspaceUi.Text("在制作区域点击新建项目，选择智能体制作并选择音频和相关材料。打开本机智能体，例如 WorkBuddy，复制提示词到对话框并发送；首次接管需在应用内批准。", 12));
        mcpBody.Children.Add(WorkspaceUi.Text("首次使用时将 MCP 配置添加到客户端。制作完成后智能体交还应用，由你审阅、保存并导出。", 12));
        var mcpSettings = new Expander
        {
            Header = Row("robot", "MCP", "连接智能体并管理授权，关闭主窗口后服务在托盘继续运行", _mcp),
            Content = mcpBody, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var apiBody = new StackPanel { Spacing = 10, Margin = new Thickness(18, 8, 18, 10) };
        var archiveActions = Actions(ActionButton("导入", ImportApiAsync), ActionButton("导出", ExportApiAsync)); archiveActions.HorizontalAlignment = HorizontalAlignment.Right; apiBody.Children.Add(archiveActions);
        apiBody.Children.Add(WorkspaceUi.Field("API Endpoint", _baseUrl)); apiBody.Children.Add(WorkspaceUi.Field("Path", _endpoint)); apiBody.Children.Add(WorkspaceUi.Field("Name", _model)); apiBody.Children.Add(WorkspaceUi.Field("Key", _key));
        var limits = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") }; limits.Children.Add(WorkspaceUi.Field("分段并发", _concurrency)); var timeout = WorkspaceUi.Field("单段超时（秒）", _timeout); Grid.SetColumn(timeout, 2); limits.Children.Add(timeout); apiBody.Children.Add(limits);
        apiBody.Children.Add(new FAInfoBar { IsOpen = true, IsClosable = false, Severity = FAInfoBarSeverity.Informational, Title = "音频处理策略", Message = "VAD 在静音处切片，每段最长 120 秒，确保请求低于 10 MB；并发与请求启动速率均限制为 10 QPS。" });
        var local = WorkspaceUi.Stack(WorkspaceUi.Field("本地模型目录", _localDirectory), _localModel, ActionButton("扫描模型", () => { var models = AppSettings.ScanLocalModels(_localDirectory.Text ?? ""); _localModel.ItemsSource = models; _localModel.SelectedIndex = models.Count > 0 ? 0 : -1; WorkspaceToast.Show(this, $"已发现 {models.Count} 个模型。"); return Task.CompletedTask; }));
        apiBody.Children.Add(new Expander { Header = "本地模型目录与选择", Content = local, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch });
        var save = ActionButton("保存 API 配置", SaveAsync, true); save.HorizontalAlignment = HorizontalAlignment.Right; apiBody.Children.Add(save);
        var apiSettings = new Expander
        {
            Header = Row("cloud", "API 设置", "配置云端转写服务、请求并发和超时", ActionButton("如何配置？", () => LaunchAsync("https://il.luyii.cn/guide.html#api-setup"))),
            Content = apiBody, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var authoring = CategoryPage("制作与智能体", "配置音频转写服务和智能体制作连接", apiSettings, mcpSettings);
        var maintenance = CategoryPage("数据与维护", "管理本机数据、缓存与诊断日志",
            Card("folder_open", "数据目录", "打开保存课程、制作工程和个人设置的文件夹", ActionButton("打开", () => LaunchAsync(AppDirectories.DataDirectory()))),
            Card("delete", "缓存", "释放可重新生成的临时文件，课程和制作数据保留", ActionButton("清理", ClearCacheAsync)),
            Card("folder_open", "日志", "警告和错误保存在数据目录的 logs 文件夹中", Actions(ActionButton("打开目录", () => LaunchAsync(AppLog.DirectoryPath)), ActionButton("清理", ClearLogsAsync))),
            Card("info", "调试模式", "开启后记录详细诊断信息", _debug));
        var privacy = CategoryPage("隐私与关于", "查看数据使用选项、协议与版本信息",
            Card("chart", "匿名使用情况分析", "向阿里云发送匿名设备 ID、ASR 指标与 API 主机名、系统与硬件信息、红色错误提示原文及运行元数据，可随时关闭", _telemetry),
            Card("document", "用户协议与隐私", "查看协议或撤回此前的同意", Actions(ActionButton("查看隐私说明", () => ShowLegalAsync("privacy_zh_cn.txt", "隐私说明")), ActionButton("撤回同意", WithdrawAsync))),
            Card("info", "关于", "查看应用版本、作者与最终用户许可协议", ActionButton("查看", AboutAsync)));

        var categories = new[]
        {
            new SettingsCategory("外观与字体", "color", appearance), new SettingsCategory("播放", "play", playback),
            new SettingsCategory("制作与智能体", "robot", authoring), new SettingsCategory("数据与维护", "folder_open", maintenance),
            new SettingsCategory("隐私与关于", "info", privacy)
        };
        var navigation = new ListBox { ItemsSource = categories, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        navigation.ItemTemplate = new FuncDataTemplate<SettingsCategory>((category, _) => category is null ? null : new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(4, 6),
            Children = { WorkspaceUi.Icon(category.Icon), WorkspaceUi.Text(category.Title) }
        });
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        navigation.SelectionChanged += (_, _) =>
        {
            if (navigation.SelectedItem is not SettingsCategory category) return;
            scroll.Content = category.Content; scroll.Offset = default;
        };
        navigation.SelectedIndex = 0;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), Margin = new Thickness(20, 0, 24, 24) };
        layout.Children.Add(navigation); Grid.SetColumn(scroll, 1); layout.Children.Add(scroll);
        var compact = false;
        void ArrangeCategories()
        {
            var next = Bounds.Width < 820;
            if (next == compact) return;
            compact = next;
            layout.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "180,*");
            layout.RowDefinitions = new RowDefinitions(compact ? "Auto,*" : "*");
            Grid.SetColumn(scroll, compact ? 0 : 1); Grid.SetRow(scroll, compact ? 1 : 0);
            navigation.ItemsPanel = new FuncTemplate<Panel?>(() => compact ? new WrapPanel { Orientation = Orientation.Horizontal } : new StackPanel());
            scroll.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(12, 0, 0, 0);
        }
        SizeChanged += (_, _) => ArrangeCategories(); ArrangeCategories();
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(WorkspaceUi.PageHeader("设置", back: () => { BackRequested?.Invoke(); return Task.CompletedTask; }));
        Grid.SetRow(layout, 1); root.Children.Add(layout); Content = root;
    }
    private void OnAgentsChanged() => Dispatcher.UIThread.Post(async () => await RunAsync(RefreshAgentsAsync));
    private void OnAgentStateChanged(bool active) => OnAgentsChanged();
    private async Task RefreshAgentsAsync()
    {
        if (_mcpServer is null) { _agents.Children.Clear(); _agents.Children.Add(WorkspaceUi.Text("智能体授权服务尚未连接。", 12)); return; }
        var registrations = await _mcpServer.GetRegisteredAgentsAsync();
        _agents.Children.Clear();
        if (registrations.Count == 0) _agents.Children.Add(WorkspaceUi.Text("尚未注册智能体。首次连接并批准接管后，授权会显示在这里。", 12));
        foreach (var agent in registrations)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(WorkspaceUi.Stack(WorkspaceUi.Text(agent.Name, 14, true), WorkspaceUi.Text(agent.Active ? "正在接管" : agent.Pending ? "等待审批" : agent.Approved ? "已授权" : "未授权 · 下次接管需审批", 12)));
            var revoke = ActionButton("撤销授权", async () => { await _mcpServer.RevokeAgentAsync(agent.Uuid); await RefreshAgentsAsync(); WorkspaceToast.Show(this, "授权已撤销，下次接管需重新审批。"); });
            revoke.IsEnabled = agent.Approved || agent.Pending;
            var remove = ActionButton("删除注册", async () =>
            {
                if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "删除智能体注册", $"删除“{agent.Name}”的注册与授权？再次使用时需要重新注册并更新客户端 MCP 配置。", "删除注册")) return;
                await _mcpServer.RevokeAgentAsync(agent.Uuid, remove: true); await RefreshAgentsAsync();
            });
            var actions = Actions(revoke, remove); actions.Margin = new Thickness(16, 0, 0, 0); Grid.SetColumn(actions, 1); row.Children.Add(actions);
            _agents.Children.Add(row);
        }
    }
    private static Control Icon(string symbol, double size = 22) => WorkspaceUi.Icon(symbol, size);
    private static Control Row(string symbol, string title, string description, Control? trailing)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,14,*,20,Auto"), MinHeight = 60, Margin = new Thickness(18, 0) };
        row.Children.Add(Icon(symbol)); var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10) }; text.Children.Add(WorkspaceUi.Text(title, 14, true)); var caption = WorkspaceUi.Text(description, 12); caption.Opacity = .85; text.Children.Add(caption); Grid.SetColumn(text, 2); row.Children.Add(text);
        if (trailing != null) { trailing.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(trailing, 4); row.Children.Add(trailing); }
        return row;
    }
    private static Control Card(string symbol, string title, string description, Control trailing) => WorkspaceUi.SettingsRow(symbol, title, description, trailing, compact: true);
    private static WrapPanel Actions(params Control[] controls) { var panel = new WrapPanel(); foreach (var c in controls) { c.Margin = new Thickness(0, 0, 8, 0); c.VerticalAlignment = VerticalAlignment.Center; panel.Children.Add(c); } return panel; }
    private Button ActionButton(string title, Func<Task> action, bool primary = false) => WorkspaceUi.Button(title, () => RunAsync(action), primary);
    private AppSettings ReadApi() => _settings with { AsrProvider = AsrProviderKind.Cloud, CloudBaseUrl = _baseUrl.Text?.Trim() ?? "", CloudEndpoint = _endpoint.Text?.Trim() ?? "", CloudModel = _model.Text?.Trim() ?? "", CloudApiKey = _key.Text?.Trim() ?? "", CloudTimeoutSeconds = (int)(_timeout.Value ?? 180), CloudConcurrency = (int)(_concurrency.Value ?? 10), CloudLanguage = "en", TranslateChineseToEnglish = true, LocalModelsDirectory = _localDirectory.Text?.Trim() ?? "", SelectedLocalModel = _localModel.SelectedItem as string ?? "", DetectedLocalModels = _localModel.ItemsSource?.OfType<string>().ToArray() ?? [] };
    public async Task SaveAsync()
    {
        await _preferenceSaves; var next = ReadApi();
        if (next.CloudBaseUrl.Length > 0 && (!Uri.TryCreate(next.CloudBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new InvalidOperationException("服务地址必须是 HTTP 或 HTTPS URL。");
        await PersistAsync(next); _settings = next; WorkspaceToast.Show(this, "API 配置已保存，云端转写配置已经更新。");
    }
    private async Task ImportApiAsync()
    {
        var path = await WorkspaceUi.Pick(this, "导入 API 配置", "zip"); if (path == null) return;
        var password = await SettingsPasswordDialog.ShowAsync(WorkspaceUi.Owner(this), false); if (password == null) return;
        await _preferenceSaves; var next = new ApiConfigurationArchive().Import(await File.ReadAllBytesAsync(path), password, _settings);
        await PersistAsync(next); Populate(next); WorkspaceToast.Show(this, "API 配置已导入，全部 API 字段已保存到 AppData。");
    }
    private async Task ExportApiAsync()
    {
        var password = await SettingsPasswordDialog.ShowAsync(WorkspaceUi.Owner(this), true); if (password == null) return;
        var path = await WorkspaceUi.Save(this, "导出 API 配置", "Intensive-Listening-API-配置.zip", "zip"); if (path == null) return;
        await File.WriteAllBytesAsync(path, new ApiConfigurationArchive().Export(ReadApi(), password)); WorkspaceToast.Show(this, "API 配置已导出，配置包已使用 AES-256 加密。");
    }
    private async Task<JsonObject> DiscoveryAsync()
    {
        var folder = Path.Combine(AppDirectories.DataDirectory(), "mcp"); var file = new[] { "server.json", "app-private-api.json" }.Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists);
        if (file == null) throw new InvalidOperationException("请先在设置中启用 MCP。");
        if (JsonNode.Parse(await File.ReadAllTextAsync(file)) is not JsonObject json || json["mcpUrl"] is not JsonValue || json["bootstrapPath"] is not JsonValue) throw new InvalidOperationException("MCP 连接信息不可用。");
        return json;
    }
    private async Task CopyAgentPromptAsync() { var discovery = await DiscoveryAsync(); await CopyAsync($"读取「{discovery["bootstrapPath"]}」文件并按其中流程操作。"); WorkspaceToast.Show(this, "一键操作提示已复制，直接发送给 AI 即可开始制作。"); }
    private async Task CopyMcpConfigurationAsync() { var discovery = await DiscoveryAsync(); var json = new JsonObject { ["mcpServers"] = new JsonObject { ["intensive-listening"] = new JsonObject { ["type"] = "http", ["url"] = discovery["mcpUrl"]!.DeepClone() } } }; await CopyAsync(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); WorkspaceToast.Show(this, "MCP 配置已复制，可粘贴到 AI 客户端的 MCP 配置中。"); }
    private async Task CopyAsync(string text) { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板不可用。"); await clipboard.SetTextAsync(text); }
    private async Task ClearLogsAsync() { if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "清理日志？", "将删除日志目录中的现有日志文件。", "清理日志")) return; var bytes = await AppLog.ClearAsync(); WorkspaceToast.Show(this, $"日志已清理，已释放 {bytes / 1024d:0.0} KB。"); }
    private async Task ClearCacheAsync() { var bytes = await AppDirectories.ClearCacheAsync(); WorkspaceToast.Show(this, $"缓存已清理，已释放 {bytes / (1024d * 1024):0.0} MB。"); }
    private async Task ShowLegalAsync(string filename, string title) => await AppDialogs.DocumentAsync(WorkspaceUi.Owner(this), title, (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "assets", "legal", filename))).TrimEnd('\0'));
    private async Task AboutAsync()
    {
        var choice = await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this), "关于 Intensive Listening", AppBuildInfo.Display + "\n数据格式 1\nBy Luyii\n界面字体：HarmonyOS Sans SC\nCopyright 2021 Huawei Device Co., Ltd.", "关闭", "字体许可", "用户协议");
        if (choice == 1) await ShowLegalAsync("HarmonyOS-Sans-LICENSE.txt", "HarmonyOS Sans 字体许可");
        if (choice == 2) await ShowLegalAsync("eula_zh_cn.txt", "用户协议");
    }
    private async Task WithdrawAsync()
    {
        if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "撤回同意", "撤回用户协议与匿名数据分析同意后，应用会停止上报、清理待发送缓存并退出。课程与本机设置会保留；下次启动需重新确认协议和遥测选择。", "撤回并退出")) return;
        await _preferenceSaves;
        if (WithdrawAgreementRequested != null) { await WithdrawAgreementRequested(); return; }
        var next = _settings with { TelemetryEnabled = false, TelemetryPrompted = false, EulaAcceptedVersion = "" }; await PersistAsync(next); _settings = next;
        var marker = Path.Combine(AppDirectories.DataDirectory(), "installation", "setup-cycle.txt"); if (File.Exists(marker)) File.Delete(marker);
        WorkspaceUi.Owner(this).Close();
    }
    private static Task LaunchAsync(string target) { if (!Directory.Exists(target) && !Uri.TryCreate(target, UriKind.Absolute, out _)) throw new IOException("目录尚未创建。"); Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return Task.CompletedTask; }
    private void ShowError(string title, Exception ex) { WorkspaceToast.Show(this, title, ex.Message, true); AppLog.Error(title, ex); }
    private async Task RunAsync(Func<Task> action) { try { await action(); } catch (Exception ex) { ShowError("操作未完成", ex); } }
}
