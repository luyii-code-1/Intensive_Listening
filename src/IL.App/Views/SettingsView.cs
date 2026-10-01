using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using IL.App.Views.Dialogs;
using IL.Core.Infrastructure;
using IL.Core.Settings;

namespace IL.App.Views;

public sealed class SettingsView : UserControl
{
    private readonly AppSettingsStore _store;
    private readonly Func<AppSettings, Task> _apply;
    private AppSettings _settings = AppSettings.Defaults();
    private Task _preferenceSaves = Task.CompletedTask;
    private bool _populating;
    private readonly TextBox _baseUrl = WorkspaceUi.Input(), _endpoint = WorkspaceUi.Input(), _model = WorkspaceUi.Input(), _key = new() { PasswordChar = '●', PlaceholderText = "输入云端转写密钥" };
    private readonly NumericUpDown _timeout = new() { Minimum = 30, Maximum = 1800, Increment = 30 }, _concurrency = new() { Minimum = 1, Maximum = 10 };
    private readonly Slider _font = new() { Minimum = 14, Maximum = 28, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 200 };
    private readonly TextBlock _fontValue = WorkspaceUi.Text("18");
    private readonly ComboBox _theme = new() { MinWidth = 145 };
    private readonly ToggleSwitch _association = new(), _mcp = new(), _skip = new(), _debug = new(), _telemetry = new();
    private readonly TextBlock _status = WorkspaceUi.Text("");
    private readonly TextBox _localDirectory = WorkspaceUi.Input();
    private readonly ComboBox _localModel = new() { MinWidth = 180 };
    private readonly DispatcherTimer _fontSave = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public Action? BackRequested { get; set; }
    public Func<Task>? WithdrawAgreementRequested { get; set; }
    public SettingsView(AppSettingsStore store, Func<AppSettings, Task>? apply = null)
    {
        _store = store; _apply = apply ?? (_ => Task.CompletedTask);
        _theme.ItemsSource = new[] { ThemeItem("跟随系统", "system"), ThemeItem("浅色模式", "light"), ThemeItem("深色模式", "dark") };
        Build(); WirePreferences();
        AttachedToVisualTree += async (_, _) => await RunAsync(ReloadAsync);
        DetachedFromVisualTree += (_, _) => { _fontSave.Stop(); if (!_populating && (int)Math.Round(_font.Value) != _settings.TranscriptFontSize) _ = ChangePreferenceAsync(s => s with { TranscriptFontSize = (int)Math.Round(_font.Value) }, "字幕字体大小"); };
    }
    public async Task ReloadAsync() { await _preferenceSaves; Populate(await _store.LoadAsync()); }
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
            _association.IsChecked = settings.FileAssociationEnabled; _mcp.IsChecked = settings.McpEnabled; _skip.IsChecked = settings.SkipOpeningPrompts;
            _debug.IsChecked = settings.DebugLogging; _telemetry.IsChecked = settings.TelemetryEnabled;
            _localDirectory.Text = settings.LocalModelsDirectory; _localModel.ItemsSource = settings.DetectedLocalModels; _localModel.SelectedItem = settings.SelectedLocalModel;
        }
        finally { _populating = false; }
    }
    private static ComboBoxItem ThemeItem(string label, string value) => new() { Content = label, Tag = value };
    private void WirePreferences()
    {
        _theme.SelectionChanged += async (_, _) => { if (!_populating && _theme.SelectedItem is ComboBoxItem { Tag: string mode }) await ChangePreferenceAsync(s => s with { ThemeMode = mode }, "外观主题"); };
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
        try { await PersistAsync(next); _status.Foreground = null; _status.Text = $"{label}已更新。"; }
        catch (Exception ex) { ShowError("设置未应用", ex); }
    }
    private async Task PersistAsync(AppSettings settings)
    {
        await _apply(settings); await _store.SaveAsync(settings);
        if (Application.Current != null) Application.Current.RequestedThemeVariant = settings.ThemeMode switch { "dark" => ThemeVariant.Dark, "light" => ThemeVariant.Light, _ => ThemeVariant.Default };
    }
    private void Build()
    {
        var cards = new StackPanel { Spacing = 8 };
        cards.Children.Add(Card("color", "外观主题", "控制应用界面的明暗外观。", _theme));
        cards.Children.Add(Card("open_file", "关联文件格式", "双击 .ilp 精听包直接进入播放界面。", _association));
        cards.Children.Add(Card("robot", "MCP", "允许本机智能体连接课程制作工具；连接后需要在应用内断开。", _mcp));
        cards.Children.Add(Card("forward", "跳过题前提示", "首次打开课程时定位到第一题前并暂停，保留已有播放进度。", _skip));
        var font = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        _fontValue.VerticalAlignment = VerticalAlignment.Center; font.Children.Add(_font); font.Children.Add(_fontValue);
        cards.Children.Add(Card("font", "字幕字体大小", "调整播放页逐句列表的文字大小。", font));
        cards.Children.Add(Card("folder_open", "数据目录", "打开保存课程、制作工程和个人设置的文件夹。", ActionButton("打开", () => LaunchAsync(AppDirectories.DataDirectory()))));
        cards.Children.Add(Card("info", "调试模式", "开启后在日志目录中记录详细诊断信息。", _debug));
        cards.Children.Add(Card("chart", "匿名使用情况分析", "向阿里云发送匿名设备 ID、ASR 指标与 API 主机名、系统与硬件信息、红色错误提示原文及运行元数据。可随时关闭。", _telemetry));
        cards.Children.Add(Card("document", "用户协议与隐私", "查看协议，或撤回此前的同意。", Actions(ActionButton("查看隐私说明", () => ShowLegalAsync("privacy_zh_cn.txt", "隐私说明")), ActionButton("撤回同意", WithdrawAsync))));
        cards.Children.Add(Card("folder_open", "日志", "警告和错误保存在数据目录的 logs 文件夹中。", Actions(ActionButton("打开日志目录", () => LaunchAsync(AppLog.DirectoryPath)), ActionButton("清理日志", ClearLogsAsync))));
        cards.Children.Add(Card("delete", "缓存", "释放可重新生成的临时文件；课程和制作数据保留。", ActionButton("清理", ClearCacheAsync)));
        cards.Children.Add(Card("info", "关于", "查看应用版本、作者与最终用户许可协议。", ActionButton("查看", AboutAsync)));
        var apiBody = new StackPanel { Spacing = 12, Margin = new Thickness(18, 12, 18, 18) };
        var archiveActions = Actions(ActionButton("导入", ImportApiAsync), ActionButton("导出", ExportApiAsync)); archiveActions.HorizontalAlignment = HorizontalAlignment.Right; apiBody.Children.Add(archiveActions);
        apiBody.Children.Add(WorkspaceUi.Field("API Endpoint", _baseUrl)); apiBody.Children.Add(WorkspaceUi.Field("Path", _endpoint)); apiBody.Children.Add(WorkspaceUi.Field("Name", _model)); apiBody.Children.Add(WorkspaceUi.Field("Key", _key));
        var limits = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") }; limits.Children.Add(WorkspaceUi.Field("分段并发", _concurrency)); var timeout = WorkspaceUi.Field("单段超时（秒）", _timeout); Grid.SetColumn(timeout, 2); limits.Children.Add(timeout); apiBody.Children.Add(limits);
        apiBody.Children.Add(new FAInfoBar { IsOpen = true, IsClosable = false, Severity = FAInfoBarSeverity.Informational, Title = "音频处理策略", Message = "VAD 在静音处切片，每段最长 120 秒，确保请求低于 10 MB；并发与请求启动速率均限制为 10 QPS。" });
        var local = WorkspaceUi.Stack(WorkspaceUi.Field("本地模型目录", _localDirectory), _localModel, ActionButton("扫描模型", () => { var models = AppSettings.ScanLocalModels(_localDirectory.Text ?? ""); _localModel.ItemsSource = models; _localModel.SelectedIndex = models.Count > 0 ? 0 : -1; _status.Text = $"已发现 {models.Count} 个模型。"; return Task.CompletedTask; }));
        apiBody.Children.Add(new Expander { Header = "本地模型目录与选择", Content = local, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch });
        var save = ActionButton("保存 API 配置", SaveAsync, true); save.HorizontalAlignment = HorizontalAlignment.Right; apiBody.Children.Add(save);
        var expansion = new Expander { Header = Row("cloud", "API 设置", "配置云端转写服务、请求并发和超时；密钥保存在本机。", ActionButton("如何配置？", () => LaunchAsync("https://il.luyii.cn/guide.html#api-setup"))), Content = apiBody, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        var guideRows = WorkspaceUi.Stack(Row("toggle_right", "1. 启用服务", "打开上方 MCP 开关，设置会立即生效。", null), Row("copy", "2. 连接 AI 客户端", "复制 MCP 配置，粘贴到 WorkBuddy 或 AI CLI 配置。", null), Row("robot", "3. 开始制作", "复制一键操作提示发送给 AI，再提供音频、试卷及答案或原文。", null), WorkspaceUi.Text("应用会先显示接管审批；制作完成后智能体返回用户模式。", 12));
        var guideHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") }; guideHeader.Children.Add(Icon("help", 16)); var guideTitle = WorkspaceUi.Text("快速使用指南", 14, true); guideTitle.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(guideTitle, 1); guideHeader.Children.Add(guideTitle); var steps = WorkspaceUi.Text("3 步", 12); Grid.SetColumn(steps, 2); guideHeader.Children.Add(steps);
        var content = new StackPanel { MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Stretch, Spacing = 12 };
        content.Children.Add(WorkspaceUi.Text("应用与集成", 20, true)); content.Children.Add(cards); expansion.Margin = new Thickness(0, 8, 0, 8); content.Children.Add(expansion);
        content.Children.Add(WorkspaceUi.Text("MCP 与智能体", 20, true)); content.Children.Add(WorkspaceUi.Text("标准 MCP 与 HTTP Tool Call 共用应用内的 Agent 会话和制作操作。", 12));
        content.Children.Add(Actions(ActionButton("复制一键操作提示", CopyAgentPromptAsync, true), ActionButton("复制MCP配置", CopyMcpConfigurationAsync), WorkspaceUi.Text("直接复制发送给 AI 即可快速开始制作", 12)));
        content.Children.Add(new Expander { Header = guideHeader, Content = guideRows, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch });
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(WorkspaceUi.PageHeader("设置", back: () => { BackRequested?.Invoke(); return Task.CompletedTask; }));
        var scroll = new ScrollViewer { Content = content, Margin = new Thickness(24, 4, 24, 24), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        _status.Margin = new Thickness(24, 0, 24, 8); Grid.SetRow(_status, 2); root.Children.Add(_status); Content = root;
    }
    private static Control Icon(string symbol, double size = 22) => WorkspaceUi.Icon(symbol, size);
    private static Control Row(string symbol, string title, string description, Control? trailing)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,14,*,20,Auto"), MinHeight = 76, Margin = new Thickness(18, 0) };
        row.Children.Add(Icon(symbol)); var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12) }; text.Children.Add(WorkspaceUi.Text(title, 14, true)); var caption = WorkspaceUi.Text(description, 12); caption.Opacity = .85; text.Children.Add(caption); Grid.SetColumn(text, 2); row.Children.Add(text);
        if (trailing != null) { trailing.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(trailing, 4); row.Children.Add(trailing); }
        return row;
    }
    private static Control Card(string symbol, string title, string description, Control trailing) => WorkspaceUi.SettingsRow(symbol, title, description, trailing);
    private static WrapPanel Actions(params Control[] controls) { var panel = new WrapPanel(); foreach (var c in controls) { c.Margin = new Thickness(0, 0, 8, 0); c.VerticalAlignment = VerticalAlignment.Center; panel.Children.Add(c); } return panel; }
    private Button ActionButton(string title, Func<Task> action, bool primary = false) => WorkspaceUi.Button(title, () => RunAsync(action), primary);
    private AppSettings ReadApi() => _settings with { AsrProvider = AsrProviderKind.Cloud, CloudBaseUrl = _baseUrl.Text?.Trim() ?? "", CloudEndpoint = _endpoint.Text?.Trim() ?? "", CloudModel = _model.Text?.Trim() ?? "", CloudApiKey = _key.Text?.Trim() ?? "", CloudTimeoutSeconds = (int)(_timeout.Value ?? 180), CloudConcurrency = (int)(_concurrency.Value ?? 10), CloudLanguage = "en", TranslateChineseToEnglish = true, LocalModelsDirectory = _localDirectory.Text?.Trim() ?? "", SelectedLocalModel = _localModel.SelectedItem as string ?? "", DetectedLocalModels = _localModel.ItemsSource?.OfType<string>().ToArray() ?? [] };
    public async Task SaveAsync()
    {
        await _preferenceSaves; var next = ReadApi();
        if (next.CloudBaseUrl.Length > 0 && (!Uri.TryCreate(next.CloudBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new InvalidOperationException("服务地址必须是 HTTP 或 HTTPS URL。");
        await PersistAsync(next); _settings = next; _status.Foreground = null; _status.Text = "API 配置已保存，云端转写配置已经更新。";
    }
    private async Task ImportApiAsync()
    {
        var path = await WorkspaceUi.Pick(this, "导入 API 配置", "zip"); if (path == null) return;
        var password = await SettingsPasswordDialog.ShowAsync(WorkspaceUi.Owner(this), false); if (password == null) return;
        await _preferenceSaves; var next = new ApiConfigurationArchive().Import(await File.ReadAllBytesAsync(path), password, _settings);
        await PersistAsync(next); Populate(next); _status.Foreground = null; _status.Text = "API 配置已导入，全部 API 字段已保存到 AppData。";
    }
    private async Task ExportApiAsync()
    {
        var password = await SettingsPasswordDialog.ShowAsync(WorkspaceUi.Owner(this), true); if (password == null) return;
        var path = await WorkspaceUi.Save(this, "导出 API 配置", "Intensive-Listening-API-配置.zip", "zip"); if (path == null) return;
        await File.WriteAllBytesAsync(path, new ApiConfigurationArchive().Export(ReadApi(), password)); _status.Foreground = null; _status.Text = "API 配置已导出，配置包已使用 AES-256 加密。";
    }
    private async Task<JsonObject> DiscoveryAsync()
    {
        var folder = Path.Combine(AppDirectories.DataDirectory(), "mcp"); var file = new[] { "server.json", "app-private-api.json" }.Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists);
        if (file == null) throw new InvalidOperationException("请先在设置中启用 MCP。");
        if (JsonNode.Parse(await File.ReadAllTextAsync(file)) is not JsonObject json || json["mcpUrl"] is not JsonValue || json["bootstrapPath"] is not JsonValue) throw new InvalidOperationException("MCP 连接信息不可用。");
        return json;
    }
    private async Task CopyAgentPromptAsync() { var discovery = await DiscoveryAsync(); await CopyAsync($"读取「{discovery["bootstrapPath"]}」文件并按其中流程操作。"); _status.Text = "一键操作提示已复制，直接发送给 AI 即可开始制作。"; }
    private async Task CopyMcpConfigurationAsync() { var discovery = await DiscoveryAsync(); var json = new JsonObject { ["mcpServers"] = new JsonObject { ["intensive-listening"] = new JsonObject { ["type"] = "http", ["url"] = discovery["mcpUrl"]!.DeepClone() } } }; await CopyAsync(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); _status.Text = "MCP 配置已复制，可粘贴到 AI 客户端的 MCP 配置中。"; }
    private async Task CopyAsync(string text) { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板不可用。"); await clipboard.SetTextAsync(text); }
    private async Task ClearLogsAsync() { if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "清理日志？", "将删除日志目录中的现有日志文件。", "清理日志")) return; var bytes = await AppLog.ClearAsync(); _status.Text = $"日志已清理，已释放 {bytes / 1024d:0.0} KB。"; }
    private async Task ClearCacheAsync() { var bytes = await AppDirectories.ClearCacheAsync(); _status.Text = $"缓存已清理，已释放 {bytes / (1024d * 1024):0.0} MB。"; }
    private async Task ShowLegalAsync(string filename, string title) => await AppDialogs.DocumentAsync(WorkspaceUi.Owner(this), title, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "assets", "legal", filename)));
    private async Task AboutAsync()
    {
        var choice = await AppDialogs.ChooseAsync(WorkspaceUi.Owner(this), "关于 Intensive Listening", "版本 2.0.0-dev\n数据格式 1\nBy Luyii", "关闭", "用户协议");
        if (choice == 1) await ShowLegalAsync("eula_zh_cn.txt", "用户协议");
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
    private void ShowError(string title, Exception ex) { _status.Foreground = Brushes.Firebrick; _status.Text = $"{title}：{ex.Message}"; AppLog.Error(title, ex); }
    private async Task RunAsync(Func<Task> action) { try { await action(); } catch (Exception ex) { ShowError("操作未完成", ex); } }
}
