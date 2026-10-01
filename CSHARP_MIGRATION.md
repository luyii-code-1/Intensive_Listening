# Intensive Listening 2.0

C# 开发位于 `2.0-dev`，行为参考同一仓库的 Dart `lib/` 与 `test/`。
优先目标是 Windows x64，同时提供 macOS ARM64 开发包。技术栈为 .NET 10、Avalonia 12、FluentAvalonia、CommunityToolkit.Mvvm，音频播放使用 LibVLC，转写音频处理使用 FFmpeg。

## 开发与构建

安装 `global.json` 指定的 .NET SDK 10.0.401。

```sh
scripts/test-csharp.sh
dotnet run --project src/IL.App
scripts/publish-csharp-windows.sh
```

macOS ARM64 包在 Apple Silicon Mac 上构建，需要本机 FFmpeg（可通过 Homebrew 安装）：

```sh
scripts/publish-csharp-macos.sh
```

输出 `artifacts/Intensive Listening 2.0.app`、`artifacts/IL2-osx-arm64.zip` 和 SHA-256。
包内包含 .NET、官方 VLC 3.0.23 ARM64 原生库、插件及本机 FFmpeg 的完整依赖。
构建脚本重写原生库加载路径，并进行 ad-hoc 签名与签名验证。构建机的 FFmpeg 8.1
要求 macOS 26.0；应用的最低系统版本从打包的原生库读取。该开发包尚未进行 Developer ID
签名和 Apple 公证。用户数据存放于 `~/Library/Application Support/Intensive Listening`。
Windows 的文件关联、ARMS SDK 和独立 EXE 导出保持 Windows 平台范围。

包内原生诊断可独立运行：

```sh
"artifacts/Intensive Listening 2.0.app/Contents/MacOS/IL.App" --verify-runtime "$PWD/artifacts/verification/macos-arm64.json"
```

本机运行窗口可用于开发；Windows 包包含 LibVLC 原生库、插件、FFmpeg、ARMS SDK 和 .NET 运行时。
输出为 `artifacts/IL2-win-x64/IL.App.exe` 与 `artifacts/IL2-win-x64.zip`，目标 Windows 无需安装 .NET。

Windows 验证与安装包制作：

```powershell
scripts/validate-csharp-windows.ps1 -RuntimeDirectory F:\dev\il2\runtime -ReportDirectory F:\dev\il2\verification
scripts/package-csharp-windows.ps1 -RuntimeDirectory F:\dev\il2\runtime -OutputDirectory F:\dev\il2\dist -InnoCompiler E:\dev\Inno\ISCC.exe
```

## 实现范围

- 学生：课程导入/更新/重名处理、校验、音频播放、逐句和题组导航、单句重听/循环、倍速/音量、字幕/挖空、进度恢复。
- 教师：工程与音频管理、分段转写与持久队列、恢复/取消/重复音频决策、识别缓存、SRT 校订、材料/题目/选项/答案、挖空同步、DOCX 试卷、ZIP/ILP/课库/独立播放器交付。
- 应用：首次设置与法律文本、设置归档、Windows 文件关联、日志、遥测选择、本机 HTTP/MCP、智能体审批与工程所有权切换。

云端 ASR 保持原 Dart 的英语识别流程；本地模型目录与字典保留原有扩展接口。原 Dart 尚未接入本地模型引擎或词典数据源。

## 数据兼容与验证

Windows 用户数据沿用 `%LOCALAPPDATA%\Intensive Listening\data`。首次读取会迁移旧根目录中的课程、工程、设置、进度、队列和 MCP 数据。
独立播放器使用单独的课程数据目录，启动参数为 `--standalone lesson.ilp`。

`tests/fixtures` 保存由原 Dart 实际生成的课包、工程、旧版工程元数据、队列和中文密码配置归档。C# 测试验证读取；生成脚本保留在测试目录或 `scripts/export-csharp-fixtures.dart`。
Windows 运行诊断为 `IL.App.exe --verify-runtime report.json`，检查原生音频与 FFmpeg、工程和课包交付，并生成可独立运行的验证课程。

UI 视觉与交互由用户验收；自动化测试与原生运行检查分别记录，不替代人工验收。

## 2026-10-01 交付记录

本机 Release 构建成功；52 项测试通过（Core 46、App 6）。原 Dart 实际生成的
ILP、工程 ZIP、v1 工程、队列、中文密码配置归档均在兼容测试中读取；C# 生成的
ILP 与配置归档也通过原 Dart 读入验证。

Windows x64 主机实际运行 .NET 10.0.12、LibVLC 3.0.24。原生播放、首次播放前跳转、
暂停后跳转、倍速、逐句循环、结束状态、错误音频拒绝与恢复、FFmpeg、工程/课包交付、
ARMS DLL 导出、独立播放器解包与嵌入课程导入均通过。完整应用的初始化日志已确认。

证据在 `artifacts/verification`；Windows 测试目录为 `F:\dev\il2-20261001`。
Windows 安装包使用现有 Inno Setup 生成，Preview 单独安装目录与 AppId。
云端 ASR 使用本机模拟 HTTP 服务验证请求、分段合并、取消与错误路径，实际云端账号
调用未包含在自动验证中。遥测验证 consent 和 SDK 导出，未在测试中提交用户数据。
UI 视觉与交互保留人工验收。

macOS ARM64 开发包在 macOS 27.0 本机完成 Release 构建，52 项测试通过。
包内 .NET 10.0.12、LibVLC 3.0.23 与 FFmpeg 8.1 已实际运行；12 项原生检查覆盖
播放/暂停/跳转/循环/结束、错误音频恢复、音频切片、ILP 与工程 ZIP 读写。
ZIP 解压到另一目录后，签名验证和相同原生检查再次通过。报告为
`artifacts/verification/macos-arm64.json` 与 `artifacts/verification/macos-relocated.json`。
应用及 ZIP 内含运行依赖，最低系统版本为 macOS 26.0。UI 视觉与交互由用户验收。

## 2026-10-01 Dart UI 还原

`2.0-dev` 按当前 `lib/main.dart` 的实际 `StudentPage`、`TeacherPage`、
`SettingsPage` 和 `lib/widgets/first_run_wizard.dart` 重新实现 C# 界面。
导航改为左侧展开/紧凑窗格；制作页恢复音频、转写、审阅、完成四阶段，
分题、题目总览与挖空各自的原布局；播放页恢复课程主页与题目/字幕双栏；
设置恢复居中卡片与折叠 API 区；任务中心和文档使用窗口内弹窗。
OOBE 恢复分步渐变面板，按已有云端配置选择五步或六步。

界面内嵌 Dart 原版思源黑体 Regular/Medium/Bold 与原图标字体。
字体诊断确认三种字重、中文、拉丁文字和图标实际解析。

可重复生成原版离屏图：

```sh
flutter test test/ui_reference_render_test.dart
```

C# 离屏生成工具与说明在 `tools/IL.UiSnapshots/README.md`。
当前基准为 Dart 11 张和 C# 两种窗口尺寸各 17 张，输出在
`artifacts/ui-reference`；`index.html` 提供并排对照。
这些图片确认离屏渲染、字体和布局接入，视觉与真实交互由用户验收。

本机 Release 构建与现有 52 项测试通过；更新的 Windows x64 包已在
外部连接地址 `10.0.0.3` 运行原生检查及独立播放器检查，全部通过。
远端目录为 `F:\dev\il2-ui-20261001`，报告复制到
`artifacts/verification/windows-ui-runtime.json`。

macOS ARM64 更新包已构建并通过 ad-hoc 签名检查。
原生检查首次在“暂停后跳转并恢复”失败，同一产物重新运行 12 项检查通过。
首跑与重跑报告分别保留为 `artifacts/verification/macos-arm64.json`、
`artifacts/verification/macos-ui-retry.json`，本次未修改原生播放实现。

Mac ZIP 解压到新目录后的签名与 12 项原生检查通过，报告为
`artifacts/verification/macos-ui-relocated.json`。

Windows SSH 会话启动探针记录到应用初始化完成，但该会话的 ANGLE 交换链创建
返回 `0x887A0022`，无法据此确认桌面显示。`windows-ui-startup.json` 中的
窗口句柄为 0；这项探针不计作界面通过。Windows 桌面视觉与交互仍待用户验收。
