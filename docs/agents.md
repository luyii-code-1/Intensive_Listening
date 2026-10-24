# 智能体制课

智能体指的是电脑上的 AI 助手程序（例如 WorkBuddy）。应用通过 **MCP** 与它连接：可以把 MCP 理解为应用给这个助手开的一条专用通道，配置一次即可反复使用。

制作时，应用把课程标题、音频和参考材料交给助手，助手整理出字幕、题目与挖空初稿；你在应用内批准它接管，结束后再逐项审阅、保存并导出。

<figure class="shot shot--diagram">
  <img src="./assets/guide/agent-flow.svg" alt="智能体制作流程：新建项目、启用 MCP、客户端连接、批准接管、智能体整理、交还审阅" loading="lazy">
  <figcaption>智能体制课的完整链路。红色为应用内操作，浅色为本机智能体客户端。</figcaption>
</figure>

## 前置条件

- 已安装桌面应用，并完成[首次运行](./first-run)。
- 本机有支持 HTTP MCP 的智能体客户端。
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

## 2. 启用 MCP 并复制配置

1. 进入「设置 → 制作与智能体」。
2. 展开 MCP 设置，打开 MCP 开关。
3. 点击「复制 MCP 配置」。
4. 点击「复制操作提示」，得到本次制作的任务说明。

<figure class="shot">
  <div class="shot-frame">
    <img src="./assets/guide/settings-agent.png" alt="MCP 设置与授权区域" loading="lazy">
  </div>
  <figcaption>开关启用后才会出现复制配置、复制操作提示与授权列表。</figcaption>
</figure>

## 3. 在客户端连接

1. 打开智能体客户端，新建或编辑 HTTP MCP 连接。
2. 粘贴上一步复制的 MCP 配置。
3. 确认客户端可以访问应用提供的本地地址。
4. 将「操作提示」粘贴到对话中并发送。

客户端首次请求会触发应用的注册流程，应用内将显示该智能体的自报名称。

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
