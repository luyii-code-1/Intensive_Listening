<div align="center">
  <img src="assets/branding/intensive_listening_mark.png" alt="Intensive Listening" width="112" />
  <h1>Intensive Listening 2 Resonance</h1>
  <p>从听力材料到精听课程，让制作与训练连成一个工作流</p>
  <p>
    <a href="https://github.com/luyii-code-1/Intensive_Listening/releases/tag/v2.0.0"><img src="https://img.shields.io/badge/Release-v2.0.0-B71C1C" alt="v2.0.0" /></a>
    <img src="https://img.shields.io/badge/Windows-10%2F11%20x64-0078D4" alt="Windows 10/11 x64" />
    <img src="https://img.shields.io/badge/macOS-26%2B%20Apple%20Silicon-555555?logo=apple" alt="macOS 26+ Apple Silicon" />
    <img src="https://img.shields.io/badge/Framework-Avalonia-8B44AC" alt="Avalonia" />
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPL--3.0--only-blue" alt="GPL-3.0-only" /></a>
  </p>
</div>

## 简介与下载

Intensive Listening 是面向英语听力教学的桌面应用。教师可以整理音频、字幕、试卷与答案，制作包含题目、时间轴和词级挖空的精听课程；学生可以按句、按题定位，反复听读、隐藏字幕并逐词揭晓答案。课程制作支持人工操作，也支持通过 MCP 交给电脑上的智能体处理，最后由用户审阅、保存与分发。

当前正式版本为 **2.0.0 · 2 Resonance**，使用 C# / .NET 10 / Avalonia 构建。

| 平台 | 安装包 | 安装方式 |
| --- | --- | --- |
| Windows 10/11 x64 | [Windows 安装包](https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.0.0/Intensive-Listening-2.0.0-win-x64-Setup.exe) | 运行安装程序，选择程序安装目录 |
| macOS 26.0+，Apple Silicon（M 系列） | [macOS DMG](https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.0.0/Intensive-Listening-2.0.0-osx-arm64.dmg) | 打开 DMG，将应用拖入 Applications |

两个安装包均包含 .NET、VLC、FFmpeg 和 ARMS 运行依赖。macOS 包当前采用 ad-hoc 签名，尚未经过 Apple 公证；系统可能要求手动允许打开。Windows 文件关联、后台托盘、接管审批系统通知和独立 EXE 播放器为 Windows 平台功能。

完整版本说明与附件见 [2 Resonance Release](https://github.com/luyii-code-1/Intensive_Listening/releases/tag/v2.0.0)。

## 2.0 的变化

本节依据 `v1.0.1` 到 2.0 发布源码的 diff 整理；下面的功能章节同时列出本版完整能力。

- **桌面架构重构**：应用迁移至 C# / .NET 10 / Avalonia，课程与工程逻辑拆分为独立核心库，保留 1.x ILP、工程 ZIP、字幕和配置归档的读取兼容。
- **Apple Silicon macOS 版本**：提供包含原生播放与音频处理依赖的 DMG，调整 macOS 渲染路径和动画调度。
- **人工与智能体制作入口**：新建项目时选择制作方式；智能体入口集中准备音频、课程标题和多份材料，并生成可复制的制作提示词。
- **可见的智能体工作流**：新增 11 步制作进度、详细状态与百分比，自报名称接管界面，以及按智能体管理注册和授权。
- **审阅与导出流程**：草稿自动保存，用户明确保存后再显示课程导出和添加到播放的操作；后续编辑会重新要求保存。
- **分类设置与应用字体**：新增设置分类导航、本机字体与字重选择、实时预览和恢复默认，统一紧凑卡片与开关样式。
- **自定义扩展按键**：单键绑定播放、句子和题目导航等七项操作，支持 PageUp / PageDown 翻页笔；仅在前台播放页生效。
- **窗口与首次设置**：首次设置铺满主窗口，稳定分页位置；完善侧栏、弹簧过渡、列表与菜单动画、鼠标位置句子操作浮窗和窗口内通知。
- **后台运行与桌面集成**：Windows 关闭主窗口后收起至托盘，MCP 和转写继续运行；单实例启动支持将打开的课程交给已有窗口。
- **安装与数据管理**：可选择安装路径，沿用 1.x 默认目录；清理旧程序与用户确认的非空目录，课程、工程和个人设置独立保存。
- **稳定性与诊断**：修复审阅、字体列表和字幕绑定等问题，完善转写完成与自动保存的并发处理，以及托管异常、原生异常和意外退出报告。
- **构建与分发**：提供 Windows 自包含构建、原生运行与独立播放器验证、安装包流水线，以及 macOS 原生依赖打包脚本。

## 课程制作

### 人工制作与审阅

- 新建空白项目或选择音频，设置课程标题，按「音频 → 转写 → 审阅 → 完成」向导制作，也可以导入现成 SRT。
- 管理多个制作工程，支持工程 ZIP 导入与导出；导入 DOCX 试卷并导出提取的 TXT 文字。
- 从音频生成带时间轴的字幕，逐句修改文字和时间点，合并、拆分句子；导入或完成转写的字幕自动绑定到工程，重新打开可继续审阅。
- 按对话、独白组织材料与小题，配置题号、题干、选项和正确答案，并绑定对应音频区间。
- 标记题前提示与重复朗读区间，在时间轴中展开或折叠查看。
- 按词点选设置挖空，同步同一材料重复朗读区间的对应挖空词。
- 自动保存编辑草稿；点击「保存」后显示「导出为精听包」与「添加到播放」，再次编辑后重新保存。
- 导出 `.ilp` 精听课程包，或直接加入本机播放列表；Windows 可导出包含课程与运行资源的独立 EXE 播放器。

### 转写与队列

- 默认接入阿里云百炼（DashScope）云端 ASR，支持自定义服务端点、模型、API Key、分段并发和单段超时。
- FFmpeg 处理音频，VAD 在静音处切片；单段最长 120 秒，控制请求体积，并限制并发与请求启动速率。
- 转写页显示任务状态、进度、分段数量和耗时，支持取消、重试、删除、清除已完成记录、载入字幕与导出 SRT。
- 持久化任务记录，重新启动后可继续被中断的任务；重复音频提供处理选择并可复用已识别字幕。
- Windows 收起主窗口后继续处理后台任务，退出时保存队列状态。

## 精听播放

- 直接打开音频、导入 `.ilp` 或从本机课程列表开始练习；Windows 支持双击关联课程文件打开。
- 保存并恢复练习进度，可配置跳过题前提示。
- 题干、选项与字幕时间轴同屏展示，切换答案显隐，跟随播放高亮当前句子。
- 按上一句、下一句、上一题、下一题定位，重听当前句，开启单句循环。
- 展开与折叠题前提示、重复朗读区间，调整题目与字幕区域的分栏。
- 隐藏字幕进行盲听，点击挖空词槽揭晓拼写，也可以批量显示或隐藏当前材料的挖空。
- 在设置中调整字幕字号（14–28）。

### 自定义扩展按键

在「设置 → 播放 → 自定义扩展按键」中，为以下操作录入单个按键，也可以清除已有绑定：

| 操作 | 适用示例 |
| --- | --- |
| 播放／暂停 | Space |
| 上一句／下一句 | PageUp / PageDown，翻页笔 |
| 上一题／下一题 | 按个人习惯选择单键 |
| 重听当前句 | 按个人习惯选择单键 |
| 显示／隐藏字幕 | 按个人习惯选择单键 |

同一个按键只绑定一项操作。绑定仅在应用窗口处于前台、当前为播放页且已有音频时生效；输入文字、编辑数值或显示阻断弹窗时暂停响应。长按按键不会连续重复触发。播放页同时保留原有 Space 播放／暂停和 Alt + 左右方向键逐句导航操作，自定义绑定优先处理。

## MCP 智能体制作

### 准备材料与交接

1. 在「制作」中新建项目，选择「智能体制作」。
2. 选择 **一个必选音频**，可设置标题，并添加多份可选试卷、听力原文或答案；材料推荐 DOCX 或文字版 PDF。
3. 启用 MCP，打开电脑上的智能体客户端（例如 WorkBuddy），按照应用提供的配置连接。
4. 点击复制提示词，粘贴到智能体对话框并发送。页面显示推荐模型 **DeepSeek-v4.1 Flash**，实际模型由用户在客户端选择。
5. 在应用内批准接管，观察制作进度；交还应用后审阅、保存，再添加到播放或导出。

材料读取、转换和识别由外部智能体选择合适方式执行；应用接收整理后的文字和课程数据。没有参考材料时，也可以基于音频完成制作。

### 授权与接管

- 应用提供 HTTP MCP 服务与内置 `MCP.md` / `SKILL.md`。客户端先检查 `/test`，注册自报名称，取得专属 UUID 和 MCP URL。
- 首次 `change_event(event: "Agent")` 请求接管时，显示 60 秒审批，可允许、拒绝或关闭 MCP；超时拒绝。
- 同一智能体获批后可复用授权。设置中的 MCP 折叠列表显示注册与授权状态，支持撤销授权和删除注册。
- 接管期间阻断用户编辑，并显示「当前 xxxx 正在接管本应用」、当前步骤、详情和进度条，用户可以强制断开。
- Windows 应用在后台时发送审批系统通知，点击通知后前置应用；前台显示应用内审批。
- 智能体调用 `change_event(event: "User")` 交还应用后，自动刷新工程并回到制作首页。

### 11 步制作进度

| 步骤 | 内容 |
| --- | --- |
| 1 | 连接与接管授权 |
| 2 | 确认音频与制作材料 |
| 3 | 恢复或创建课程工程 |
| 4 | 绑定音频与转写 |
| 5 | 提取与导入资料 |
| 6 | 校对字幕与时间轴 |
| 7 | 划分材料与设置题目 |
| 8 | 检查提示与重复朗读 |
| 9 | 确认与设置挖空 |
| 10 | 回读、校验与保存 |
| 11 | 清理临时文件与交还应用 |

智能体通过 `report_authoring_step` 上报 `running`、`completed`、`skipped`、`failed` 和详细说明。完成及跳过的步骤计入百分比；转写与资料提取可以并行进行。

### 制作接口

| 环节 | 主要工具 |
| --- | --- |
| 音频与转写 | `import_project_media`、`start_project_asr` |
| 参考资料 | `import_project_text`、`read_project_text` |
| 字幕校对 | `read_project_srt`、`set_cue_text`、`import_project_srt` |
| 材料与题目编排 | `auto_plan_questions`、`apply_question_plan` |
| 词级挖空与校验 | `apply_cloze_plan`、`validate_course_project` |
| 制作进度与接管 | `report_authoring_step`、`change_event` |

字幕与资料支持分页读取，题目方案可以一次提交材料、选项、答案、提示和重复朗读区间。具体参数与制作规则以应用返回的 MCP 工具定义和制作 Skill 为准。

## 设置、首次启动与数据

设置页提供五个分类导航：

| 分类 | 功能 |
| --- | --- |
| 外观与字体 | 跟随系统／浅色／深色，本机字体、字重、可编辑预览、恢复默认，即时应用 |
| 播放 | 跳过题前提示、字幕字号、自定义扩展按键 |
| 制作与智能体 | 云端 ASR 配置、密码加密的 ZIP 配置导入导出、本地模型目录扫描与选择、MCP 开关及授权管理 |
| 数据与维护 | 打开数据目录、清理缓存与日志、调试日志开关 |
| 隐私与关于 | 匿名数据分析选择、隐私说明、协议与撤回同意、版本和构建信息 |

首次启动或安装后，在主窗口中完成 OOBE：确认协议，配置适用的文件关联、MCP、转写 API 与匿名数据分析。匿名数据分析可随时关闭；崩溃报告提供报告 ID、版本、平台、组件与错误堆栈，支持复制，按同意状态上报或补报。

Windows 默认安装到 `Program Files\Intensive Listening`，支持选择其他路径。检测到 1.x 程序目录时清理旧程序；其他非空目录须由用户确认删除后再安装。安装程序保护系统目录和应用数据目录。

课程、制作工程、设置与进度独立保存在用户数据目录：

- Windows：`%LOCALAPPDATA%\Intensive Listening\data`
- macOS：`~/Library/Application Support/Intensive Listening`

Windows 首次启动会迁移旧数据目录中的设置、课程库、工程、进度与转写队列。支持读取旧版 ILP、工程 ZIP、SRT 和配置归档；兼容性说明见 [迁移与兼容说明](docs/MIGRATION.md)。

Windows 关闭主窗口会收起到托盘，后台服务继续运行；从托盘选择「退出」结束应用。重复启动或打开课程文件会交给已有实例。窗口内成功通知显示 5 秒，错误通知显示 15 秒，并支持复制错误信息。

## 云端转写配置

学生打开已有 `.ilp` 课程练习无需配置 ASR。需要自动转写时，打开「设置 → 制作与智能体 → API 设置」，填入百炼 API Key，保存配置。

| 配置项 | 默认值 |
| --- | --- |
| API Endpoint | `https://dashscope.aliyuncs.com` |
| Path | `/api/v1/services/aigc/multimodal-generation/generation` |
| Name | `qwen-audio-3.0-asr-flash` |
| Key | 用户自己的 API Key，本机保存 |
| 分段并发 | 10，可设置 1–10 |
| 单段超时 | 180 秒 |

API Key 可在 [阿里云百炼控制台](https://bailian.console.aliyun.com/) 创建。请求使用该 Key 对应的账户与额度；已有 SRT 时可以直接导入。应用提供本地模型目录管理，目前内置转写执行使用云端服务。

## 从源码运行与构建

需要 .NET 10 SDK，具体版本见 `global.json`。Windows 打包需要 Inno Setup 6；macOS 打包需在 Apple Silicon Mac 上安装 FFmpeg。原生资源下载与打包由脚本完成。

```bash
git clone https://github.com/luyii-code-1/Intensive_Listening.git
cd Intensive_Listening
dotnet run --project src/IL.App
```

测试与 Windows 自包含运行包：

```bash
dotnet test IL2.slnx -c Release
bash scripts/publish-csharp-windows.sh
```

在 Windows 上解压运行包并生成安装程序：

```powershell
Expand-Archive artifacts/build/IL2-win-x64.zip artifacts/runtime
./scripts/package-csharp-windows.ps1 -RuntimeDirectory (Resolve-Path artifacts/runtime).Path -OutputDirectory "$PWD/artifacts/installer" -InnoCompiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

Apple Silicon macOS 应用与 DMG：

```bash
bash scripts/publish-csharp-macos.sh
```

Windows 运行包写入 `artifacts/build/`，两个平台安装包均写入 `artifacts/installer/`。`artifacts/` 为本机生成目录；可使用 `bash scripts/clean-generated.sh` 清理中间产物。

### 分支与 CI

`main` 为发布与默认分支，`2.0-dev` 用于日常开发，PR 目标选择 `2.0-dev`。发布时运行 **Publish 2.0-dev snapshot to main**，将远端开发分支的完整文件树写入以旧 `main` 为父提交的新提交，并验证文件树一致。

**C# Windows build** 在开发与发布分支提交、PR、手动触发及 `v2.*` Release 发布时运行，完成现有测试、自包含构建、Windows 原生运行、独立播放器、单实例与 Inno Setup 安装包验证。发布工作流将 Windows 安装程序附加到 Release；macOS 使用本机打包脚本生成 DMG。

### 技术与源码结构

| 领域 | 实现 |
| --- | --- |
| 界面 | C#、.NET 10、Avalonia、FluentAvalonia、CommunityToolkit.Mvvm |
| 播放与音频处理 | LibVLCSharp / VLC、FFmpeg、VAD、DashScope ASR |
| 课程与持久化 | 本地工程、ZIP、ILP、SRT、JSON 设置与任务队列 |
| 智能体 | HTTP MCP、工具调用、内置握手说明与制作 Skill |
| 桌面与诊断 | Windows 托盘／通知／文件关联、单实例、崩溃监控、可选 ARMS 遥测 |

```text
src/IL.App/       界面、ViewModel、播放与桌面集成
src/IL.Core/      课程、工程、转写、MCP、设置与遥测
src/IL.Launcher/  Windows 独立课程播放器启动器
tests/           自动测试与旧版数据兼容夹具
tools/           离屏界面与动画验证工具
assets/          图标、字体、许可与品牌资源
third_party/     原生依赖来源与许可证
scripts/         构建、打包、验证与产物清理
docs/            迁移、兼容与诊断说明
```

1.x 的 Dart / Flutter 源码、测试和夹具生成脚本保存在 [`archive/dart-1.x`](https://github.com/luyii-code-1/Intensive_Listening/tree/archive/dart-1.x) 分支，存档说明见该分支的 `DART_ARCHIVE.md`。

## 反馈与许可

欢迎通过 [Issues](https://github.com/luyii-code-1/Intensive_Listening/issues) 提交反馈。问题报告请提供应用版本、操作系统、复现步骤、预期和实际表现，以及日志、报告 ID 或截图。代码贡献请说明使用场景并运行现有测试。

Copyright © 2026 Luyii。源码使用 [GPL-3.0-only](LICENSE)，第三方字体、FFmpeg 和其他组件适用各自许可证，见 [第三方许可说明](THIRD_PARTY_NOTICES.md)。
