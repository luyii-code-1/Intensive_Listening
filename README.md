<div align="center">
  <img src="assets/branding/intensive_listening_mark.png" alt="Intensive Listening" width="112" />
  <h1>Intensive Listening</h1>
  <p>面向英语听力教学的材料制作与逐句训练工具</p>
  <p>
    <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4?logo=windows" alt="Windows 10/11" />
    <img src="https://img.shields.io/badge/Framework-Flutter-02569B?logo=flutter" alt="Flutter" />
    <img src="https://img.shields.io/badge/Status-Early%20Development-E7A33E" alt="Early Development" />
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPL--3.0--only-blue" alt="GPL-3.0-only" /></a>
  </p>
</div>

## 项目简介

Intensive Listening 是一款面向英语听力教学的 Windows 桌面应用，将听力材料整理、题目编排与逐句练习整合为连贯的课程制作与学习流程。

教师可以从音频和字幕建立课程项目，校对句子、划分题目并设置挖空；学生可以按句或按题播放课程，在统一的时间轴中完成精听练习。项目数据保存在本地，云端转写和 AI 辅助制作按需启用。

> [!IMPORTANT]
> 项目目前处于早期开发阶段，交互、接口和课程文件格式仍会持续调整。现阶段适合参与开发、功能试用与问题反馈。

## 当前能力

### 课程制作

- 以项目形式管理音频、字幕、题目和制作进度
- 导入音频或 SRT 字幕，并在时间轴中校对句子
- 使用 VAD 切分音频，调用可配置的 ASR 服务完成转写
- 按听力结构划分题目，编辑题号、题干和句子范围
- 以单词为单位设置挖空，并同步处理重复朗读内容
- 生成 `.ilp` 精听课程包或加入本地课程列表

### 精听练习

- 打开常见音频文件和 `.ilp` 课程包
- 按句、按题定位播放，支持单句循环和进度恢复
- 展示题目、题前提示与当前句子
- 支持字幕隐藏、挖空显示和学习记录保存
- 根据播放位置自动跟随当前内容

### 自动化实验

- 提供标准 MCP 接口与 HTTP Tool Call 接口
- Agent 会话可以读取项目、导入媒体、触发转写和编排题目
- 应用内显示 Agent 工作状态，并保留人工接管入口

## 制作流程

```text
创建项目 → 导入音频 → 转写或导入字幕 → 校对与分题 → 设置挖空 → 生成课程
```

学生打开课程后，可以在题目与句子之间定位，结合字幕和挖空状态反复练习。播放位置与练习记录会随课程保存在本地。

## 从源码运行

### 开发环境

- Windows 10 或 Windows 11 x64
- Flutter stable，并启用 Windows Desktop
- Visual Studio 2022，安装“使用 C++ 的桌面开发”工作负载
- FFmpeg 9.0.1 essentials x64

### 获取源码

```powershell
git clone https://github.com/luyii-code-1/Intensive_Listening.git
cd Intensive_Listening
flutter pub get
```

从 [GyanD/codexffmpeg 9.0.1](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.1) 获取 Windows x64 essentials 构建，将 `ffmpeg.exe` 放入：

```text
windows/third_party/ffmpeg/ffmpeg.exe
```

依赖版本与校验值记录在 [windows/third_party/ffmpeg/README.md](windows/third_party/ffmpeg/README.md)。

### 运行与构建

```powershell
flutter run -d windows
```

```powershell
flutter analyze
flutter test
flutter build windows --release
```

## 技术组成

| 领域 | 实现 |
| --- | --- |
| 桌面界面 | Flutter、fluent_ui |
| 音频播放 | media_kit、Windows 原生播放器集成 |
| 音频处理 | FFmpeg |
| 项目与课程 | 本地文件、ZIP 容器、`.ilp` 课程格式 |
| 自动化接口 | MCP、HTTP Tool Call |

## 源码结构

```text
assets/    字体、证书、许可文本与品牌资源
lib/       Flutter 应用源码
test/      单元测试与组件测试
windows/   Windows Runner 与原生集成源码
```

## 参与项目

欢迎通过 [Issues](https://github.com/luyii-code-1/Intensive_Listening/issues) 提交问题、使用反馈和功能建议。有效的问题报告通常包括：

- 应用版本与 Windows 版本
- 可重复的操作步骤
- 预期行为与实际表现
- 必要的日志或界面截图

提交代码前，请运行 `flutter analyze` 和 `flutter test`，并说明变更覆盖的使用场景。

## 许可证

Copyright © 2026 Luyii。

本项目源代码采用 [GNU General Public License v3.0](LICENSE)（GPL-3.0-only）许可。字体、FFmpeg 与其他第三方组件适用各自的许可证。
