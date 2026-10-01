# 第三方组件声明

Intensive Listening 以 GNU General Public License v3.0 发布，同时包含或下载了以下受各自许可证约束的第三方组件。

## FFmpeg

Windows 版内置 Gyan Doshi 编译的 FFmpeg 9.0.1 essentials 构建，许可证为 GPLv3。
许可证文本在 `windows/third_party/ffmpeg/LICENSE`，版本锁定和校验和记录在
`windows/third_party/ffmpeg/README.md`。

## 思源黑体 (Source Han Sans)

内置的 Source Han Sans CN 字体文件版权归 Adobe (2014-2021) 所有，采用 SIL Open Font
License 1.1 授权，许可证文本在 `assets/fonts/LICENSE.txt`。

## Flutter 和 Dart 包

Flutter、Dart 以及 `pubspec.lock` 中列出的各个三方包保留其原有版权声明和许可条款。

### C# 界面复用的原版图标

C# 2.0 界面内嵌原 Dart 应用所用的 `fluent_ui` 4.16.1 `FluentIcons.ttf`，
并保留同包的 BSD-3-Clause 声明于 `assets/legal/fluent_ui_license.txt`。
图标字体用于保持原版图标。C# 2.0 的界面文字使用 HarmonyOS Sans SC。

## 阿里云 ARMS RUM PC SDK

**Alibaba Cloud ARMS RUM PC SDK**

Provider: Alibaba Cloud

License: Proprietary / Alibaba Cloud Terms

Usage: Real User Monitoring / Crash & telemetry reporting

Windows 版内置官方 RUM PC SDK 0.4.4 的 x64 DLL，macOS 构建准备了同版本的 ARM64 动态库。macOS 遥测当前暂未启用。下载来源及 SHA-256 记录在
`windows/third_party/arms/README.md`。应用仅在用户明确授权后加载 SDK，
使用 SDK 默认采集配置：开启 libcurl 网络请求与原生崩溃采集，CEF 采集保持默认关闭。
用户关闭匿名数据分析后，应用关闭 SDK 并清理待发送缓存。

## C# 2.0 桌面组件

Avalonia 12.1.3、FluentAvalonia 3.0.1、CommunityToolkit.Mvvm 8.4.0 与 SharpZipLib
1.4.2 使用 MIT 许可证；LibVLCSharp 3.10.1 与 LibVLC Windows 3.0.24 使用
LGPL-2.1-or-later。版本、来源见 `assets/legal/csharp-third-party.txt`，
LGPL 文本见 `assets/legal/LGPL-2.1.txt`。Windows 包保留各组件的动态库与来源声明。

## HarmonyOS Sans

C# 2.0 内嵌华为原始 HarmonyOS Sans SC Regular、Medium、Bold 字体，未修改字体文件。
Copyright 2021 Huawei Device Co., Ltd.
完整许可保留于 `assets/legal/HarmonyOS-Sans-LICENSE.txt`。
官方来源：https://developer.huawei.com/images/download/general/HarmonyOS-Sans.zip
字体包 SHA256：`fb02c86e358cd9aad8d4dfa957ee502381e7ee2e94499a9133add4324b6ce69a`。
