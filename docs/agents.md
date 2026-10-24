# 智能体制课

智能体指的是电脑上的 AI 助手程序（例如 WorkBuddy）。应用通过 **MCP** 把制作能力交给它：连接由应用自己准备，你只要在应用里打开开关、复制一段提示词发给助手。

制作时，应用把课程标题、音频和参考材料交给助手，助手整理出字幕、题目与挖空初稿；你在应用内批准它接管，结束后再逐项审阅、保存并导出。

<figure class="shot shot--diagram">
  <img src="./assets/guide/agent-flow.svg" alt="智能体制作流程：新建项目、启用 MCP、粘贴提示词、批准接管、智能体整理、交还审阅" loading="lazy">
  <figcaption>智能体制课的完整链路。红色为应用内操作，浅色为本机智能体客户端。</figcaption>
</figure>

## 前置条件

- 已安装桌面应用，并完成[首次运行](./first-run)。
- 本机装有智能体客户端，例如 WorkBuddy。
- 已准备音频；可选准备试卷、答案或听力原文。

## 1. 新建智能体项目

1. 在左侧导航进入「制作」。
2. 点击右上角「新建项目」。
3. 在对话框中选择「智能体制作」。
4. 选择必需的音频文件。
5. 填写课程标题，并按需加入多份试卷或听力原文。
6. 确认材料列表后进入下一步。

<figure class="shot">
  <div class="shot-frame">
    <img src="./assets/guide/authoring-mode.png" alt="新建项目对话框" loading="lazy">
  </div>
  <figcaption>材料推荐 DOCX 或文字版 PDF；音频为必选项。</figcaption>
</figure>

## 2. 启用 MCP

1. 打开「设置 → 制作与智能体」，找到 MCP 分组。
2. 打开开关；停在「智能体制作」页时，也可以直接点页面上的「启用 MCP」。
3. 开关会保留，下次启动应用无需再打开。

<figure class="shot">
  <img src="./assets/guide/settings-agent.png" alt="设置中的 MCP 开关" loading="lazy">
  <figcaption>打开开关即可，地址与令牌由应用自动处理。</figcaption>
</figure>

## 3. 复制提示词，发给智能体

1. 回到「制作 → 智能体制作」，确认标题、音频与材料已经填好。
2. 在页面下方点击「复制提示词」。按钮不可用时，先点旁边的「启用 MCP」。
3. 打开 WorkBuddy，把提示词粘贴到对话框并发送。
4. 智能体按提示读取应用生成的说明，提交自报名称，然后申请接管。

<figure class="shot shot--diagram">
  <img src="./assets/guide/agent-client.svg" alt="智能体对话示意图：粘贴提示词并发送后，智能体登记自报名称并申请接管" loading="lazy">
  <figcaption>示意图：提示词较长，客户端会折行显示。发送后的第一次请求会让应用登记这个智能体，并弹出接管确认。</figcaption>
</figure>

::: tip 发送后没有反应
- 应用是否仍在运行，MCP 开关是否打开。
- 提示词是否整段粘贴：开头是「请使用本机 Intensive Listening 应用制作一份精听课程」。
- 接管确认只在 60 秒内有效，超时需要在客户端重新发起。
:::

## 4. 批准接管

1. 智能体发起接管时，应用显示智能体名称与 60 秒授权窗口。
2. 核对名称是否与当前客户端一致。
3. 在 60 秒内选择允许，接管随即开始。

<figure class="shot">
  <img src="./assets/guide/agent-approval.png" alt="智能体请求接管弹窗，包含拒绝、批准接管与关闭 MCP 三个选项" loading="lazy">
  <figcaption>批准接管弹窗。「WorkBuddy 桌面版」是示例智能体名称，实际显示客户端自报的名称。</figcaption>
</figure>

::: warning 授权范围
接管期间应用会锁定编辑，并显示当前制作步骤与进度。名称或来源不明确时请拒绝；授权可在 MCP 设置中随时撤销，或删除注册记录。
:::

## 5. 查看制作进度

1. 制作过程共 11 步，覆盖音频与材料、转写、字幕校对、题目编排、重复朗读、挖空、校验与交还。
2. 应用显示每一步的状态、详情与整体百分比。
3. 转写与资料提取可以并行执行，无需等待其中一项完成。

<figure class="shot">
  <img src="./assets/guide/agent-progress.png" alt="智能体制作进度面板，显示百分比、已完成步骤与进行中的步骤" loading="lazy">
  <figcaption>进度面板：上方是整体百分比，下方是步骤列表，列表可滚动查看后续步骤。</figcaption>
</figure>

## 6. 交还后审阅与交付

1. 智能体调用交还后，应用自动刷新课程工程。
2. 打开工程，逐题核对字幕、题干、选项、答案与挖空。
3. 需要修改时直接编辑，完成后点击「保存」。
4. 选择「导出为精听包」或「添加到播放」。

智能体结果属于初稿，字幕文字与答案必须人工确认。保存与导出细节见[人工制课](./authoring#_6-保存并导出)。
