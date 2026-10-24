# student_app 项目说明（面向智能体）

Intensive Listening 2 Resonance 桌面应用仓库。教师端把音频与试卷做成带时间轴、题目和词级挖空的 `.ilp` 精听包；学生端逐句精听。C# / .NET 10 / Avalonia / FluentAvalonia / CommunityToolkit.Mvvm。

工作区级约定见上级目录 `AGENTS.md`；本文件只覆盖本仓库。

## 工具链

| 项 | 路径 / 版本 |
| --- | --- |
| .NET SDK | `E:\dev\dotnet\dotnet.exe`，`global.json` 固定 `10.0.401` |
| Inno Setup | `E:\dev\Inno\ISCC.exe` |
| Python | `C:\Python313\python.exe` |
| Node | PATH 上的 Node 22 |
| 构建入口 | `scripts/build-csharp-windows.ps1` |

只在用户明确要求时构建；范围以当次请求为准。构建前先核对分支与已有未提交改动。

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/IL.Core/` | 领域核心：`Ilp`、`Projects`、`Asr`、`Transcription`、`Playback`、`Student`、`Settings`、`Management`（MDN）、`Mcp`、`Updates`、`Telemetry` |
| `src/IL.App/` | Avalonia 界面：`Views/`、`ViewModels/`、`Services/`（播放、托盘、单实例、更新） |
| `src/IL.Launcher/` | 独立课程播放器启动器 |
| `tests/IL.Core.Tests`、`tests/IL.App.Tests` | xUnit 测试 |
| `tools/IL.UiSnapshots` | 离屏界面与动效验证 |
| `mdn_server/` | Python 集控服务端（FastAPI + PyInstaller） |
| `scripts/` | 构建、打包、版本脚本 |
| `.github/` | 发布与同步工作流 |
| `docs/` | 版本、CI、MDN、迁移等说明 |

## 构建与测试

```powershell
# 编译 + 全部测试（默认 Insider 身份）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-csharp-windows.ps1

# 只跑测试
dotnet test IL2.slnx -c Release

# 生成 Windows 安装包（需先产出运行目录）
./scripts/package-csharp-windows.ps1 -RuntimeDirectory <运行目录> -InnoCompiler 'E:\dev\Inno\ISCC.exe'
```

CI 里 Windows 运行时构建由 `scripts/publish-csharp-windows.sh` 完成（含测试、FFmpeg/ARMS 校验、`build-info.json`），macOS 由 `scripts/publish-csharp-macos.sh` 在 Apple Silicon 上完成。两个包都输出到工作区 `dist`。

MDN 打包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File mdn_server/build-windows.ps1 -Python C:\Python313\python.exe
```

## 版本

- 唯一数值来源：`Directory.Build.props` 的 `DesktopBaseVersion`（当前 `2.2.0`）。
- `scripts/build_version.py` 生成 `artifacts/version/build.json` 与 `BuildVersion.props`；后者被 props 自动 import。**不要手改生成文件**，也不要提交 `artifacts/`。
- 渠道：
  - Release → `2.2.0`（标签 `v2.2.0`）
  - 日常 → `2.2.0 Insider Preview MMDD N`（`2.2.0-insider.YYYYMMDD.N`）
  - Pre-Release → `2.2.0 Beta MMDD`（`2.2.0-beta.YYYYMMDD`）
- 界面显示不带 `v`；Git 标签、更新签名保留原始串。产物名用 `artifactVersion`（如 `2.2.0-Insider-Preview-1023-2`）。
- `AppBuildInfo.Version`（三段）、`UpdateVersion`（完整身份）、`DisplayVersion`（界面）分别用于协议、更新比较和显示。
- 仍有手写版本常量：`AppDirectories`、`CrashReports`、`AppPrivateApiServer`（两处）、`AppTelemetry` 默认值、`AppBuildInfo` 兜底、`mdn_server/server.py`。改基础版本时同步这些位置。
- 版本解析器（`ManagedVersion`、`mdn_server/desktop_version.py`）接受带 `v` 前缀与旧四段数值；桌面构建链只接受三段 base。

## CI 与发布

工作流：

| 工作流 | 触发 | 作用 |
| --- | --- | --- |
| `csharp-windows-build.yml` | push `2.0-dev`/`main`、PR、Release、手动 | 版本作业 → Linux 便携构建 → Windows 测试/验证/安装包 → macOS DMG；Release 时上传附件 |
| `release-metadata.yml` | `workflow_run`（默认分支 `main`） | 把最新正式 Release 写入 `website` 分支的 `downloads/latest.json` 与 `release.js` 兜底标签 |
| `sync-dev-to-main.yml` | `workflow_run`、手动 | 正式 Release 构建成功后，把该标签的源码快照以 PR 形式同步到 `main` |

要点：

- Release 事件使用**默认分支 `main`** 上的工作流定义，`actions/checkout` 取的是 **tag** 的内容；因此工作流文件必须先合入 `main`，版本脚本与工作流必须兼容。
- 正式发布顺序：升 `DesktopBaseVersion` → 提交并推送 `2.0-dev` → 打 `vX.Y.Z` → 发布 Release → 构建并上传 Windows/macOS 附件 → 元数据工作流更新官网 → 同步 PR 更新 `main`。
- 需要的凭据：`IL_SYNC_TOKEN`（同步 PR）。仓库里的 `IL_DEPLOY_*` 目前没有工作流使用。
- 官网 `website` 分支由 EdgeOne 用 `node scripts/build.mjs` 构建，只接受「Latest + 非草稿 + 非预发布 + 三段」标签，页面显示不带 `v`。
- Web 播放器（`web-edition` 分支）有独立版本线，`sw.js` 的 `CACHE_NAME` 是唯一来源，经 `web.json` 同步到官网。

## 数据与兼容

- 用户数据：Windows `%LOCALAPPDATA%\Intensive Listening\data`；macOS `~/Library/Application Support/Intensive Listening`。
- 读取兼容 1.x 的 `.ilp`、工程 ZIP、SRT 与配置归档；`.ilp` 当前格式版本 3，仍接受版本 2 并原位升级。
- MCP 服务由应用在运行时生成 `MCP.md` / `SKILL.md`（用户数据目录 `mcp/`），工具签名以运行时文件为准。
- 更新链路比较完整构建身份（基础版本、渠道、日期、同日序号），并保留原有防降级规则。

## 约定

- 提交信息描述实际结果；不把会话里的临时方案写进产物或说明。
- 不提交 `artifacts/`、`bin/`、`obj/` 和生成目录。
- `2.0-dev` 承载日常开发；`main` 只接收正式 Release 快照与发布流水线定义，改动走 PR。
- 修改行为时补或跑相关测试；UI 交互验收由用户完成。
