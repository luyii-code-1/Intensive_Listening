using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using System.Text.Json;
using IL.App.Views.Dialogs;
using IL.Core.Infrastructure;
using IL.Core.Mcp;

namespace IL.App.Views;

internal sealed class AgentAuthoringView : UserControl
{
    private readonly AppPrivateApiServer _server;
    private readonly Func<Task> _enableMcp;
    private readonly TextBox _title = WorkspaceUi.Input(hint: "选择音频后自动填写，也可以自行修改");
    private readonly TextBlock _audioName = WorkspaceUi.Text("尚未选择音频", 14, true);
    private readonly TextBlock _audioLocation = WorkspaceUi.Text("选择一个本机音频文件。", 12);
    private readonly StackPanel _materialList = new() { Spacing = 6 };
    private readonly TextBlock _mcpState = WorkspaceUi.Text("", 12);
    private readonly Button _copy, _enable;
    private readonly List<string> _materials = [];
    private string? _audio;

    public AgentAuthoringView(AppPrivateApiServer server, Func<Task> enableMcp)
    {
        _server = server; _enableMcp = enableMcp;
        _copy = WorkspaceUi.Button("复制提示词", () => RunAsync(CopyPromptAsync), true);
        _enable = WorkspaceUi.Button("启用 MCP", () => RunAsync(async () => { await _enableMcp(); UpdateMcp(); }));
        var body = new StackPanel { Spacing = 16, MaxWidth = 840, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(WorkspaceUi.Field("项目标题", _title));
        var audio = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        audio.Children.Add(WorkspaceUi.Stack(_audioName, _audioLocation));
        var pick = WorkspaceUi.Button("选择音频", () => RunAsync(PickAudioAsync));
        pick.Margin = new Thickness(16, 0, 0, 0); Grid.SetColumn(pick, 1); audio.Children.Add(pick);
        body.Children.Add(WorkspaceUi.Field("听力音频 · 必选，限一个文件", WorkspaceUi.Surface(audio)));
        body.Children.Add(WorkspaceUi.Field("相关材料 · 可选，可选择多个文件", WorkspaceUi.Stack(
            WorkspaceUi.Text("试卷、听力原文、答案，推荐 DOCX 或文字版 PDF 文件。", 12),
            _materialList, WorkspaceUi.Button("选择材料", () => RunAsync(PickMaterialsAsync)))));
        body.Children.Add(new Separator());
        body.Children.Add(WorkspaceUi.Text("交给智能体制作", 18, true));
        body.Children.Add(WorkspaceUi.Text("选择完成后，打开电脑上的智能体，例如 WorkBuddy；复制提示词，粘贴到智能体对话框并发送。在应用内批准接管后，智能体即可开始制作。"));
        body.Children.Add(WorkspaceUi.Text("推荐模型：DeepSeek-v4.1 Flash", 14, true));
        body.Children.Add(_mcpState);
        body.Children.Add(WorkspaceUi.Row(_copy, _enable, WorkspaceUi.Button("复制 MCP 配置", () => RunAsync(CopyConfigurationAsync))));
        body.Children.Add(WorkspaceUi.Text("首次使用时，可将 MCP 配置添加到智能体客户端。制作完成后回到应用审阅、保存并导出课程。", 12));
        Content = new ScrollViewer { Content = body, Margin = new Thickness(24, 4, 24, 24), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        AttachedToVisualTree += (_, _) => UpdateMcp();
        RenderMaterials(); UpdateMcp();
    }
    private async Task PickAudioAsync()
    {
        var audio = await WorkspaceUi.Pick(this, "选择课程音频", "mp3", "wav", "m4a");
        if (audio is null) return;
        if (string.IsNullOrWhiteSpace(_title.Text) || _title.Text == Path.GetFileNameWithoutExtension(_audio))
            _title.Text = Path.GetFileNameWithoutExtension(audio);
        _audio = audio; _audioName.Text = Path.GetFileName(audio); _audioLocation.Text = audio; UpdateMcp();
    }
    private async Task PickMaterialsAsync()
    {
        var files = await WorkspaceUi.Owner(this).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择试卷、听力原文或答案", AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null && !_materials.Contains(path, StringComparer.OrdinalIgnoreCase)) _materials.Add(path);
        }
        RenderMaterials();
    }
    private void RenderMaterials()
    {
        _materialList.Children.Clear();
        foreach (var path in _materials)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(WorkspaceUi.Stack(WorkspaceUi.Text(Path.GetFileName(path), 14, true), WorkspaceUi.Text(path, 12)));
            var remove = WorkspaceUi.IconButton("delete", "移除材料", () => { _materials.Remove(path); RenderMaterials(); return Task.CompletedTask; });
            ToolTip.SetTip(remove, "移除材料"); Grid.SetColumn(remove, 1); row.Children.Add(remove); _materialList.Children.Add(row);
        }
        if (_materials.Count == 0) _materialList.Children.Add(WorkspaceUi.Text("尚未选择相关材料。", 12));
    }
    private void UpdateMcp()
    {
        _enable.IsVisible = !_server.IsRunning;
        _copy.IsEnabled = _audio is not null && _server.IsRunning;
        _mcpState.Text = _server.IsRunning ? "MCP 已启用，可以复制提示词。" : "请启用 MCP 后复制提示词。";
    }
    private async Task CopyPromptAsync()
    {
        if (!_server.IsRunning) throw new InvalidOperationException("请先启用 MCP。");
        if (_audio is null || !File.Exists(_audio)) throw new IOException("请选择一个可读取的音频文件。");
        foreach (var path in _materials) if (!File.Exists(path)) throw new IOException($"材料文件已移动或删除：{Path.GetFileName(path)}");
        await CopyAsync(AgentAuthoringPrompt.Create(_server.BootstrapPath, _audio, _title.Text, _materials));
        WorkspaceToast.Show(this, "提示词已复制", "打开智能体对话框，粘贴并发送即可开始制作。", false);
    }
    private async Task CopyConfigurationAsync()
    {
        var json = new { mcpServers = new Dictionary<string, object> { ["intensive-listening"] = new { type = "http", url = _server.McpUrl } } };
        await CopyAsync(JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        WorkspaceToast.Show(this, "MCP 配置已复制。");
    }
    private async Task CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板不可用。");
        await clipboard.SetTextAsync(text);
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { WorkspaceToast.Show(this, "操作未完成", ex.Message, true); AppLog.Error("智能体制作引导失败", ex); }
    }
}
