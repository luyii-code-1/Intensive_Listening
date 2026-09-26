const agentHelpFileName = 'SKILL.md';
const agentBootstrapFileName = 'MCP.md';

String agentBootstrapMarkdown(String mcpUrl) =>
    '''# Intensive Listening MCP

1. 保持 Intensive Listening 运行，并在设置中启用 MCP。
2. 使用以下地址连接标准 HTTP MCP：`$mcpUrl`。
3. 调用 `intensive_listening_status` 测试连接。`PendingApproval` 表示等待应用内审批；收到 `pending_approval` 时稍后重试。`UserRefused` / `user_refused` 表示用户拒绝本次接管。
4. 获得 `event=Agent` 后，读取状态返回的 `helpPath` 所指向的 `SKILL.md`，再开始制作。
5. 制作结束调用 `end_agent_session`（HTTP Tool Call 也可使用 `/v1/agent/disconnect`），确认 `event=User`。

所有路径是应用所在电脑的本机绝对路径。MCP 操作只使用当前会话返回的项目 ID 与字幕索引。
''';

const agentHelpMarkdown = '''---
name: intensive-listening-course-authoring
description: Create an Intensive Listening course from local audio and exam documents through the app MCP.
---

# 精听课程制作

## 会话与输入

1. 调用 `intensive_listening_status`；只有 `event=Agent` 时写入。若仍为 `PendingApproval`，等待用户审批；若为 `UserRefused`，停止本次制作。
2. 用户通常提供一个音频及两份 DOCX 或可提取文本的 PDF（试卷、答案或听力原文）。若用户尚未提供任何文件，立即暂停制作并向用户索要文件；在收到文件前不要创建工程、启动 ASR 或推测素材。首次发现部分文件缺失时，集中向用户询问，并说明题目、答案或字幕校对可能不完整。音频缺失时等待音频；文档缺失时可在说明后继续有依据的部分，不编造题目或答案。扫描版 PDF 无可用文本时请用户换用可提取文字的文件。
3. 这些文件与应用位于同一台电脑。读取文件名生成清晰课程名；先调用 `list_course_projects`，复用同一音频和试卷对应的工程，再考虑 `create_course_project`。

## 转写与资料

4. 用 `import_project_media` 绑定音频。项目已有字幕或正在转写时复用状态；否则调用一次 `start_project_asr`。通过 `get_course_project(fields: [])` 轮询，避免重复提交。
5. ASR 运行时，用本机 Python 把 DOCX / 可复制文字的 PDF 转为 UTF-8 TXT，并调用 `import_project_text(role: exam|reference, textPath, sourcePath)` 保存试卷与答案/原文。用 `read_project_text` 分页读取。转换产生的临时文件在项目成功保存后清理；原件和工程文件保留。
6. ASR 完成后分页调用 `read_project_srt`。以音频为准，对照原文检查句子、词间空格、漏词和时间点。单句文字修正用 `set_cue_text`；需要增删、拆分或合并时间段时，用 Python 编辑 UTF-8 SRT，在设置题目之前调用 `import_project_srt(mode: replace)`，再读取新 cue 索引。不得猜测旧索引仍有效。

## 题目、提示与挖空

7. 一段对话或独白是一段材料，可含多道小题。试卷决定真实题号、完整题干和选项；答案文档决定 `answerIndex`（从 0 开始）。使用 `apply_question_plan(materials[])` 原子提交：每段材料包含 `cueIndexes`、`questions[]`，可包含 `leadInCueIndexes` 与 `repeatedCueIndexes`。题前播报 cue 绑定到紧随其后的材料，播放该提示时仍显示该材料的题目；纯全卷说明保持未归属。
8. 需要初稿时可先调用 `auto_plan_questions`，随后依据试卷和原文修正。音频播放第二遍时，两遍仍属于同一材料；用 `repeatedCueIndexes` 标记第二遍，并把两遍对应句子的字幕校正为完全相同的文本。挖空词在两遍保持一致。只使用工具回读的 cue 和 word 索引。
9. 分题完成后询问用户是否自动设置挖空。用户同意时，依据 `read_project_srt` 的词索引调用 `apply_cloze_plan`；用户拒绝时保留空挖空。词元索引不包含标点。

## 完成

10. 回读工程并调用 `validate_course_project`。向用户报告课程名、工程 ID、题目数、答案与挖空状态，以及需复核的缺件或歧义。默认仅保存制作工程；只有用户要求时才加入学生端或导出文件。
11. 将 event 设回 User：调用 `end_agent_session` 或 HTTP `/v1/agent/disconnect`。确认返回 `event=User`；应用会刷新工程列表并显示制作首页。清理转换临时目录，保留用户原件与工程。
''';

String agentConnectionInstructions(String helpPath) =>
    '连接已获批准。先读取 SKILL.md：$helpPath；随后调用 intensive_listening_status。';
