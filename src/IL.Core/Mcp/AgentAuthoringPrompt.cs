using System.Text.Encodings.Web;
using System.Text.Json;

namespace IL.Core.Mcp;

public static class AgentAuthoringPrompt
{
    public static string Create(string bootstrapPath, string audioPath, string? title, IReadOnlyList<string> materialPaths)
    {
        var input = JsonSerializer.Serialize(new
        {
            title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(audioPath) : title.Trim(),
            audioPath,
            materialPaths
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return $"""
请使用本机 Intensive Listening 应用制作一份精听课程。
先读取「{bootstrapPath}」，按其中的流程连接 MCP、提交你的自报名称并请求用户批准接管；获批后读取应用返回的 SKILL.md，再执行制作。

本次制作输入如下，所有文件都位于本机：
{input}

采用输入中的标题，使用指定音频，并结合所选试卷、听力原文和答案制作课程。材料为可选输入：有材料时按文件内容识别用途并提取文字；材料列表为空时直接基于音频完成可完成的制作。其他格式由你选择适合的读取、转换或识别方式，遇到无法解决的问题再询问用户。
先查询已有工程，复用对应素材的工程，或创建新工程。完成转写、字幕校对、题目与答案编排，并按 SKILL.md 与用户确认挖空设置。
完成后校验并保存制作工程，报告结果，再调用 change_event(event: "User") 交还应用。加入播放与导出由用户在应用内操作。
""";
    }
}
