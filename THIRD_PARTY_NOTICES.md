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

## 阿里云 ARMS RUM PC SDK

**Alibaba Cloud ARMS RUM PC SDK**

Provider: Alibaba Cloud

License: Proprietary / Alibaba Cloud Terms

Usage: Real User Monitoring / Crash & telemetry reporting

Windows 版内置官方 RUM PC SDK 0.4.4 的 x64 DLL。下载来源及 SHA-256 记录在
`windows/third_party/arms/README.md`。应用仅在用户明确授权后加载 SDK，
并关闭了 SDK 的自动网络与原生崩溃采集。
