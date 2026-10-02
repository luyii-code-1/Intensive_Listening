namespace IL.Core.Mcp;
public static class AgentHelp
{
public const string Markdown = """
---
name: intensive-listening-course-authoring
description: Create an Intensive Listening course from local audio and exam documents through the app MCP.
---

# 精听课程制作

## 步骤进度上报

下列编号 1 至 11 同时是应用接管界面中的步骤编号。获得接管授权后，应用自动完成第 1 步。开始每一步时调用 `report_authoring_step(step, status: "running", detail)`；真正完成并保存相关结果后提交 `completed`。无需执行的可选工作提交 `skipped` 并说明原因；失败提交 `failed` 与具体原因，修复重试时重新提交 `running`。`detail` 写清正在处理的内容或完成结果，最多 1000 字符，供用户直接阅读。

第 4 步 ASR 与第 5 步资料提取可并行进行，分别更新状态；仅启动 ASR 不算完成第 4 步，须等待字幕可读取。第 8 步仍需检查初稿与朗读情况，没有重复朗读可说明检查结果。没有材料可跳过第 5 步；用户拒绝挖空则跳过第 9 步。等待用户回答时保持 `running`，在详情中说明等待内容。不得提前报告尚未完成的工作。

调用 `intensive_listening_status` 可读取 `authoringProgress` 中的完整步骤状态。进度按已完成或已跳过步骤数 / 11 计算。第 11 步先完成临时文件清理并上报，再调用 `change_event(event: "User")` 交还应用；不把跳过、失败或断开连接当作制作成功。

## 会话与输入

1. 按 MCP.md 完成 `/test`、注册与 `change_event(event: "Agent")`，然后调用 `intensive_listening_status`；只有 `event=Agent` 时写入。若仍为 `PendingApproval`，等待用户审批；若为 `UserRefused`，停止本次制作。
2. 用户提供一个音频，试卷、答案或听力原文为可选材料，推荐 DOCX 或文字版 PDF。先读取制作提示词中的标题及文件清单；音频缺失时集中询问并等待音频。用户明确选择仅音频制作时，直接完成有依据的内容；不编造题目或答案。其他格式由智能体选择适合的本机转换、文字提取或 OCR 方式；遇到无法解决的问题再询问用户。
3. 这些文件与应用位于同一台电脑。读取文件名生成清晰课程名；先调用 `list_course_projects`，复用同一音频和试卷对应的工程，再考虑 `create_course_project`。

## 转写与资料

4. 用 `import_project_media` 绑定音频。项目已有字幕或正在转写时复用状态；否则调用一次 `start_project_asr`。通过 `get_course_project(fields: [])` 轮询，避免重复提交。
5. ASR 运行时，根据材料格式选择本机文字提取、转换或 OCR 方式转为 UTF-8 TXT，并调用 `import_project_text(role: exam|reference, textPath, sourcePath)` 保存试卷与答案/原文。用 `read_project_text` 分页读取。转换产生的临时文件在项目成功保存后清理；原件和工程文件保留。
6. ASR 完成后分页调用 `read_project_srt`。以音频为准，对照原文检查句子、词间空格、漏词和时间点。单句文字修正用 `set_cue_text`；需要增删、拆分或合并时间段时，用 Python 编辑 UTF-8 SRT，在设置题目之前调用 `import_project_srt(mode: replace)`，再读取新 cue 索引。不得猜测旧索引仍有效。

## 题目、提示与挖空

7. 一段对话或独白是一段材料，可含多道小题。试卷决定真实题号、完整题干和选项；答案文档决定 `answerIndex`（从 0 开始）。使用 `apply_question_plan(materials[])` 原子提交：每段材料包含 `cueIndexes`、`questions[]`，可包含 `leadInCueIndexes` 与 `repeatedCueIndexes`。题前播报 cue 绑定到紧随其后的材料，播放该提示时仍显示该材料的题目；纯全卷说明保持未归属。
8. 需要初稿时可先调用 `auto_plan_questions`，随后依据试卷和原文修正。音频播放第二遍时，两遍仍属于同一材料；用 `repeatedCueIndexes` 标记第二遍，并把两遍对应句子的字幕校正为完全相同的文本。挖空词在两遍保持一致。只使用工具回读的 cue 和 word 索引。
9. 分题完成后询问用户是否自动设置挖空。用户同意时，依据 `read_project_srt` 的词索引调用 `apply_cloze_plan`；用户拒绝时保留空挖空。词元索引不包含标点。

## 完成

10. 回读工程并调用 `validate_course_project`。向用户报告课程名、工程 ID、题目数、答案与挖空状态，以及需复核的缺件或歧义。默认仅保存制作工程；只有用户要求时才加入学生端或导出文件。
11. 清理转换临时目录，保留用户原件与工程；上报本步完成后，将 event 设回 User：调用 `change_event(event: "User")`。确认返回 `event=User`；应用会刷新工程列表并显示制作首页。
""";
private const string BootstrapTemplate = """
# Intensive Listening MCP

1. 保持 Intensive Listening 运行，并在设置中启用 MCP。先访问 `http://127.0.0.1:17683/test`；只有返回 `{"status":"ok","version":"x.y.z"}` 才继续。未连接时请用户检查 MCP 开关与应用进程，不要猜测端口占用原因。
2. 使用以下地址连接标准 HTTP MCP：`$mcpUrl`。如果客户端尚未配置 MCP，请把该地址加入客户端的 HTTP MCP 配置，再执行连接。
3. 第一次连接调用 `register_agent`，提交自己的名称。应用返回分配的 `agentUuid` 和专属 `mcpUrl`。保存 UUID，并把客户端 MCP URL 改为返回的专属 URL 后重新连接。不要把 UUID 当作公开名称传播。
4. 调用 `change_event(event: "Agent")` 请求接管。`PendingApproval` 表示等待应用内审批；`UserRefused` 表示用户拒绝或超时，请询问用户是否重新尝试。若用户关闭 MCP，停止连接并让用户自行重新开启。相同 UUID 首次批准后可直接进入 Agent 模式。
5. 获得 `event=Agent` 后，读取状态返回的 `helpPath` 所指向的 `SKILL.md`，再开始制作。制作结束调用 `change_event(event: "User")`，确认 `event=User`。

所有路径是应用所在电脑的本机绝对路径。MCP 操作只使用当前会话返回的项目 ID 与字幕索引。
""";
public static string Bootstrap(string url)=>BootstrapTemplate.Replace("$mcpUrl",url);
}
