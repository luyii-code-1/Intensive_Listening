const agentHelpFileName = 'HELP.md';

const agentHelpMarkdown = '''# Intensive Listening Agent Help

HELP version: 1.3

本文件是 Intensive Listening 制作接口的会话指南。连接 MCP 或 HTTP Tool Call 后，先读取本文件，再开始操作工程。

## 会话规则

1. 先调用 `intensive_listening_status`，确认 `event=Agent`。参数不确定时读取 `GET /v1/tools` 的完整 JSON Schema；用户点击“强制断开”后事件会变为 `User`，后续写操作将返回 `agent_required`，此时停止操作并等待用户重新连接。
2. 只使用工具返回的工程 ID、cue 索引和 word 索引；工程变化后重新读取。
3. 优先恢复已有工程，避免重复创建、重复导入和重复提交 ASR。
4. 数据采用“听力材料 → 多道小题”两层结构。同一段对话或独白的 cue 只归属一个材料，材料可包含一道或多道小题；考试说明、章节播报、倒计时、`Text XXX` 和纯旁白保持未归题。
5. 写入题目或挖空后调用 `validate_course_project`；校验通过后默认调用 `add_project_to_playback`。
6. 大工程使用字段选择和分页。轮询状态时调用 `get_course_project(fields: [])`；读取字幕时用 `read_project_srt(offset, limit, includeSrt: false)` 分页，避免反复传输完整 SRT。

## 推荐流程

1. `list_course_projects`：查找可恢复工程。
2. `create_course_project` / `get_course_project`：创建或读取工程。新建与增量新建工具会返回创建出的 ID，后续直接使用返回值。
3. `import_exam_document(projectId, documentPath)`，然后分页调用 `read_exam_text(offset, limit)`：导入并读取 DOCX 试卷；`documentPath` 必须是本机 DOCX 绝对路径。
4. `import_project_media(projectId, mediaPath)`：绑定听力音频。必须先绑定音频，才能调用 `import_project_srt(projectId, srtPath)`。
5. `start_project_asr`：只在没有字幕和现有任务时提交一次；随后轮询 `get_course_project`。完成后 SRT 自动写入工程，`questionPlanPending=true` 表示可继续整理题目。
6. 分页调用 `read_project_srt(offset, limit, includeSrt: false)`：读取权威 cue、时间、分段和单词索引。若有听力原文，逐段对照原文与 SRT 的结构和文字；检查词间空格、断词、漏词、句间衔接及重复朗读。用 `set_cue_text` 修正有依据的识别错误，再重新读取受影响的 cue 与词索引。只有确实需要原始 SRT 文本时才设置 `includeSrt: true`。
7. 根据试卷与校正后的 SRT 整理题目：可先调用 `auto_plan_questions(projectId)` 生成可编辑初稿；有试卷时优先调用 `apply_question_plan`，按试卷顺序一次写入材料、小题与 cue 归属。提交 `materials[]` 时，每项包含 `cueIndexes` 与 `questions[]`；存在第二遍朗读时，用 `repeatedCueIndexes` 标明第二遍的 cue。
8. `apply_cloze_plan`：按 word 索引设置挖空。
   写入后可用 `get_course_project(projectId, fields:["cloze"])` 回读挖空索引。
9. `validate_course_project`：复核题目、cue 唯一归属和挖空索引。
10. `add_project_to_playback`：制作完成后直接加入学生端；仅在用户要求文件时导出。
    精听包只包含音频、字幕与练习数据，试卷 DOCX/TXT 保留在制作工程中。

## 题目与挖空

- 小题文本和题号以试卷为准，SRT 负责材料音频定位。
- Agent 发起转写后，由 `auto_plan_questions` 显式生成可编辑初稿；结合试卷语义和时间顺序校正。
- “听下面一段对话，回答第 6 和第 7 小题”应建立一段材料，在该材料下建立第 6、7 两道小题；切换这两道题时不改变播放位置。
- “听下面两段录音……”一类提示保留在原始 cue 流或材料元数据中，不要把它单独建立为页面标题、章节或题目。题前提示只放未归属任何材料的考试说明或旁白，不要把同一材料按答案句拆成多个互斥题组。
- 自动规划会把 `1 2 - 1 3`、全角数字和中文题号规范为真实题号；最终题号仍以试卷为准。
- 原文存在时逐句核对 SRT：每个英文词应当完整、有意义，词间空格正确，连续读起来自然通顺；结合上下文修正 ASR 的粘词、拆词和误识别。原文缺失或与音频冲突时以实际听到的内容为准，不凭题目补造台词。
- 对单题材料尤其检查是否整段播放两遍：两遍属于同一材料和同一道题，第二遍 cue 放入 `repeatedCueIndexes`，避免生成重复题目。第一遍与第二遍对应句子的字幕文本必须完全一致；先用 `set_cue_text` 校正，再提交题目计划。音频未重复时无需设置该字段。
- 标点不参与分词；重复朗读的同一句保持一致的挖空设置。

## 增量修正与索引

- 单句字幕修正使用 `set_cue_text(projectId, cueIndex, text)`。它保留时间轴、题目归属，并自动移除超出新分词长度的挖空索引。
- 整份字幕导入使用 `import_project_srt`。默认 `mode=auto`：时间轴完全一致时合并文本并保留题目/有效挖空；时间轴变化时替换并返回 `preserved`、`cleared` 统计。需要强制清空制作数据时才用 `mode=replace`。
- `add_question_group` 返回 `createdGroupId` 与 `createdMaterialId`；后续编辑直接使用返回的 ID，不要通过再次读取后猜测。
- 应用的词索引只包含英文单词与带撇号/连字符的英文词，标点不占索引。无法确定时调用 `tokenize_lesson_text(text)`，并严格使用其返回的 `index`。

## 完成报告

向用户简要报告课程名称、工程 ID、题目数量、挖空句子数和交付结果。只有无法可靠对齐且不同选择会实质改变课程时才请求用户确认。
''';

String agentConnectionInstructions(String helpPath) =>
    '连接成功。调用任何课程制作工具前，必须先读取 HELP.md：$helpPath。'
    'HELP.md 是当前版本的权威制作流程；读取后先调用 intensive_listening_status。';
