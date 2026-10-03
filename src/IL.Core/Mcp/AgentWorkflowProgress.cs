using System.Text.Json.Nodes;

namespace IL.Core.Mcp;

public sealed record AgentWorkflowStep(int Number, string Title, string Description, string Status = "pending", string? Detail = null);
public sealed record AgentWorkflowSnapshot(IReadOnlyList<AgentWorkflowStep> Steps)
{
    public int CompletedSteps => Steps.Count(s => s.Status is "completed" or "skipped");
    public int Percent => (int)Math.Round(CompletedSteps * 100d / Steps.Count);
    public JsonObject ToJson() => new()
    {
        ["completedSteps"] = CompletedSteps, ["totalSteps"] = Steps.Count, ["percent"] = Percent,
        ["steps"] = new JsonArray(Steps.Select(s => (JsonNode)new JsonObject
        {
            ["step"] = s.Number, ["title"] = s.Title, ["description"] = s.Description,
            ["status"] = s.Status, ["detail"] = s.Detail
        }).ToArray())
    };
}

public sealed class AgentWorkflowProgress
{
    private readonly object _gate = new();
    private AgentWorkflowStep[] _steps = CreateSteps();
    public event Action? Changed;
    public AgentWorkflowSnapshot Snapshot { get { lock (_gate) return new(_steps.ToArray()); } }
    private static AgentWorkflowStep[] CreateSteps() =>
    [
        new(1, "连接与接管授权", "连接 MCP、完成注册与审批，读取应用状态"),
        new(2, "确认音频与制作材料", "读取课程标题、音频、试卷、原文和答案清单"),
        new(3, "恢复或创建课程工程", "查找同一音频对应的工程，确定课程名称"),
        new(4, "绑定音频与转写", "导入音频，复用字幕或等待 ASR 转写完成"),
        new(5, "提取与导入资料", "提取试卷、答案和听力原文，保存并分页读取文本"),
        new(6, "校对字幕与时间轴", "对照音频和原文检查字幕、漏词、分句与时间点"),
        new(7, "划分材料与设置题目", "绑定音频片段，填写题号、题干、选项和答案"),
        new(8, "检查提示与重复朗读", "校正题前提示和第二遍朗读，检查两遍字幕一致性"),
        new(9, "确认与设置挖空", "征询挖空选择，按字幕词索引提交挖空方案"),
        new(10, "回读、校验与保存", "校验课程工程，报告题目、答案、挖空及待复核内容"),
        new(11, "清理临时文件与交还应用", "清理转换临时文件，保留原件与工程，结束本次接管")
    ];
    public void BeginSession()
    {
        lock (_gate) { _steps = CreateSteps(); _steps[0] = _steps[0] with { Status = "completed" }; }
        Changed?.Invoke();
    }
    public AgentWorkflowSnapshot Report(int step, string status, string? detail)
    {
        if (step is < 1 or > 11) throw new AppPrivateApiException("invalid_step", "步骤编号须为 1 至 11");
        if (status is not ("running" or "completed" or "skipped" or "failed"))
            throw new AppPrivateApiException("invalid_step_status", "步骤状态须为 running、completed、skipped 或 failed");
        if (detail?.Length > 1000) throw new AppPrivateApiException("invalid_step_detail", "步骤详情最多 1000 个字符");
        AgentWorkflowSnapshot snapshot;
        lock (_gate)
        {
            _steps[step - 1] = _steps[step - 1] with { Status = status, Detail = detail?.Trim() };
            snapshot = new(_steps.ToArray());
        }
        Changed?.Invoke();
        return snapshot;
    }
}
