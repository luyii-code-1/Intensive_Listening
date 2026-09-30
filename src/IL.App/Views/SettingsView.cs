using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using IL.App.Views.Dialogs;
using IL.Core.Infrastructure;
using IL.Core.Settings;

namespace IL.App.Views;

public sealed class SettingsView : UserControl
{
    private readonly AppSettingsStore _store;
    private readonly Func<AppSettings, Task> _apply;
    private AppSettings _settings = AppSettings.Defaults();
    private readonly TextBox _baseUrl = WorkspaceUi.Input(), _endpoint = WorkspaceUi.Input(), _model = WorkspaceUi.Input(), _key = new() { PasswordChar = '●' };
    private readonly NumericUpDown _timeout = new() { Minimum = 30, Maximum = 1800, Increment = 30, Width = 160 }, _concurrency = new() { Minimum = 1, Maximum = 10, Width = 160 }, _font = new() { Minimum = 14, Maximum = 28, Width = 160 };
    private readonly ComboBox _theme = new() { ItemsSource = new[] { "system", "light", "dark" }, MinWidth = 160 };
    private readonly CheckBox _translate = new() { Content = "将识别的中文翻译为英文" }, _association = new() { Content = "关联 .ilp 文件" }, _mcp = new() { Content = "启用本机 MCP 制作接口" }, _skip = new() { Content = "首次打开课程时跳过题前提示" }, _debug = new() { Content = "记录调试日志" }, _telemetry = new() { Content = "允许匿名数据分析" };
    private readonly TextBlock _status = WorkspaceUi.Text("");
    private readonly TextBox _localDirectory = WorkspaceUi.Input();
    private readonly ComboBox _localModel = new() { MinWidth = 180 };
    public SettingsView(AppSettingsStore store, Func<AppSettings, Task>? apply = null)
    {
        _store = store; _apply = apply ?? (_ => Task.CompletedTask); Build();
        AttachedToVisualTree += async (_, _) => await RunAsync(ReloadAsync);
    }
    public async Task ReloadAsync() { _settings = await _store.LoadAsync(); Populate(_settings); }
    public void Populate(AppSettings settings)
    {
        _settings = settings; _baseUrl.Text = settings.CloudBaseUrl; _endpoint.Text = settings.CloudEndpoint; _model.Text = settings.CloudModel; _key.Text = settings.CloudApiKey;
        _timeout.Value = settings.CloudTimeoutSeconds; _concurrency.Value = settings.CloudConcurrency; _font.Value = settings.TranscriptFontSize; _theme.SelectedItem = settings.ThemeMode;
        _translate.IsChecked = settings.TranslateChineseToEnglish; _association.IsChecked = settings.FileAssociationEnabled; _mcp.IsChecked = settings.McpEnabled; _skip.IsChecked = settings.SkipOpeningPrompts; _debug.IsChecked = settings.DebugLogging; _telemetry.IsChecked = settings.TelemetryEnabled;
        _localDirectory.Text = settings.LocalModelsDirectory; _localModel.ItemsSource = settings.DetectedLocalModels; _localModel.SelectedItem = settings.SelectedLocalModel;
    }
    private void Build()
    {
        var api = WorkspaceUi.Stack(WorkspaceUi.Text("云端转写", 18, true), WorkspaceUi.Text("填写服务地址、请求路径、模型与 API Key。识别语言为英语。", 12),
            WorkspaceUi.Field("服务地址", _baseUrl), WorkspaceUi.Field("请求路径", _endpoint), WorkspaceUi.Field("模型", _model), WorkspaceUi.Field("API Key", _key),
            WorkspaceUi.Row(WorkspaceUi.Field("请求超时（秒）", _timeout), WorkspaceUi.Field("并发分段", _concurrency)), _translate,
            WorkspaceUi.Row(WorkspaceUi.Button("导入加密配置", () => RunAsync(ImportApiAsync)), WorkspaceUi.Button("导出加密配置", () => RunAsync(ExportApiAsync)),
                WorkspaceUi.Button("配置说明", () => RunAsync(() => LaunchAsync("https://il.luyii.cn/guide.html#api-setup")))));
        var general = WorkspaceUi.Stack(WorkspaceUi.Text("播放与外观", 18, true), WorkspaceUi.Field("主题", _theme), WorkspaceUi.Field("字幕字号", _font), _skip,
            new Separator(), WorkspaceUi.Text("本机集成", 16, true), _association, _mcp, WorkspaceUi.Text("MCP 与 HTTP 制作入口只在本机监听，智能体接管会请求应用内批准。", 12),
            new Separator(), WorkspaceUi.Text("隐私与诊断", 16, true), _telemetry, WorkspaceUi.Text("匿名分析用于了解使用情况与错误；课程内容、音频、字幕、试卷与密钥保存在本机。", 12), _debug,
            WorkspaceUi.Row(WorkspaceUi.Button("用户协议", () => RunAsync(() => ShowLegalAsync("eula_zh_cn.txt", "用户协议"))), WorkspaceUi.Button("隐私说明", () => RunAsync(() => ShowLegalAsync("privacy_zh_cn.txt", "隐私说明")))));
        var storage = WorkspaceUi.Stack(WorkspaceUi.Text("存储", 18, true), WorkspaceUi.Field("应用数据目录", new SelectableTextBlock { Text = AppDirectories.DataDirectory(), TextWrapping = Avalonia.Media.TextWrapping.Wrap }),
            WorkspaceUi.Row(WorkspaceUi.Button("打开数据目录", () => RunAsync(() => LaunchAsync(AppDirectories.DataDirectory()))), WorkspaceUi.Button("打开日志目录", () => RunAsync(() => LaunchAsync(AppLog.DirectoryPath)))),
            WorkspaceUi.Row(WorkspaceUi.Button("清理日志", () => RunAsync(ClearLogsAsync)), WorkspaceUi.Button("清理缓存", () => RunAsync(ClearCacheAsync))),
            new Separator(), WorkspaceUi.Text("本地模型目录", 16, true), _localDirectory, _localModel, WorkspaceUi.Button("扫描模型", () => RunAsync(() =>
            { var models = AppSettings.ScanLocalModels(_localDirectory.Text ?? ""); _localModel.ItemsSource = models; _localModel.SelectedIndex = models.Count > 0 ? 0 : -1; _status.Text = $"已发现 {models.Count} 个模型。"; return Task.CompletedTask; })),
            WorkspaceUi.Text("当前制作流程使用云端 API。模型目录与选择保存在本机配置中。", 12),
            new Separator(), WorkspaceUi.Text("Intensive Listening 2.0", 16, true), WorkspaceUi.Text("课程制作 · 字幕审阅 · 精听播放", 12));
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,28,*,28,*"), MaxWidth = 1200, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        content.Children.Add(api); Grid.SetColumn(general, 2); content.Children.Add(general); Grid.SetColumn(storage, 4); content.Children.Add(storage);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(WorkspaceUi.Row(WorkspaceUi.Text("设置", 22, true), WorkspaceUi.Button("保存设置", () => RunAsync(SaveAsync), true), WorkspaceUi.Button("重新加载", () => RunAsync(ReloadAsync))));
        var scroll = new ScrollViewer { Content = content, Margin = new Thickness(0, 14, 0, 0) }; Grid.SetRow(scroll, 1); root.Children.Add(scroll); Grid.SetRow(_status, 2); root.Children.Add(_status);
        Content = root; Padding = new Thickness(24);
    }
    private AppSettings Read() => _settings with
    {
        CloudBaseUrl = _baseUrl.Text?.Trim() ?? "", CloudEndpoint = _endpoint.Text?.Trim() ?? "", CloudModel = _model.Text?.Trim() ?? "", CloudApiKey = _key.Text?.Trim() ?? "",
        CloudTimeoutSeconds = (int)(_timeout.Value ?? 180), CloudConcurrency = (int)(_concurrency.Value ?? 10), TranslateChineseToEnglish = _translate.IsChecked == true,
        ThemeMode = _theme.SelectedItem as string ?? "system", TranscriptFontSize = (int)(_font.Value ?? 18), SkipOpeningPrompts = _skip.IsChecked == true,
        FileAssociationEnabled = _association.IsChecked == true, FileAssociationPrompted = true, McpEnabled = _mcp.IsChecked == true, DebugLogging = _debug.IsChecked == true,
        TelemetryEnabled = _telemetry.IsChecked == true, TelemetryPrompted = true, LocalModelsDirectory = _localDirectory.Text?.Trim() ?? "", SelectedLocalModel = _localModel.SelectedItem as string ?? "",
        DetectedLocalModels = _localModel.ItemsSource?.OfType<string>().ToArray() ?? []
    };
    public async Task SaveAsync()
    {
        var next = Read();
        if (next.CloudBaseUrl.Length > 0 && (!Uri.TryCreate(next.CloudBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new InvalidOperationException("服务地址必须是 HTTP 或 HTTPS URL。");
        if (_settings.TelemetryEnabled && !next.TelemetryEnabled && !await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "关闭匿名数据分析", "关闭后停止提交匿名使用与错误数据，仍可使用全部课程功能。", "关闭")) { _telemetry.IsChecked = true; return; }
        await _apply(next); await _store.SaveAsync(next); _settings = next;
        if (Application.Current != null) Application.Current.RequestedThemeVariant = next.ThemeMode switch { "dark" => ThemeVariant.Dark, "light" => ThemeVariant.Light, _ => ThemeVariant.Default };
        _status.Text = "设置已保存。";
    }
    private async Task ImportApiAsync()
    {
        var path = await WorkspaceUi.Pick(this, "导入 API 配置", "zip"); if (path == null) return;
        var password = await AppDialogs.PasswordAsync(WorkspaceUi.Owner(this), "解密 API 配置"); if (password == null) return;
        var next = new ApiConfigurationArchive().Import(await File.ReadAllBytesAsync(path), password, Read()); Populate(next); await SaveAsync(); _status.Text = "API 配置已导入。";
    }
    private async Task ExportApiAsync()
    {
        var password = await AppDialogs.PasswordAsync(WorkspaceUi.Owner(this), "加密 API 配置"); if (password == null) return;
        var path = await WorkspaceUi.Save(this, "导出 API 配置", "api-config.zip", "zip"); if (path == null) return;
        await File.WriteAllBytesAsync(path, new ApiConfigurationArchive().Export(Read(), password)); _status.Text = "API 配置已导出。";
    }
    private async Task ClearLogsAsync()
    {
        if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "清理日志", "删除日志目录中的现有日志文件。", "清理")) return;
        var bytes = await AppLog.ClearAsync(); _status.Text = $"日志已清理，释放 {bytes / 1024d:0.0} KB。";
    }
    private async Task ClearCacheAsync()
    {
        await AppDirectories.ClearCacheAsync(); _status.Text = "缓存已清理。";
    }
    private async Task ShowLegalAsync(string filename, string title)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "legal", filename);
        await AppDialogs.DocumentAsync(WorkspaceUi.Owner(this), title, await File.ReadAllTextAsync(path));
    }
    private static Task LaunchAsync(string target) { if (Directory.Exists(target) || Uri.TryCreate(target, UriKind.Absolute, out _)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); else throw new IOException("目录尚未创建。"); return Task.CompletedTask; }
    private async Task RunAsync(Func<Task> action) { try { await action(); } catch (Exception ex) { _status.Text = ex.Message; } }
}
