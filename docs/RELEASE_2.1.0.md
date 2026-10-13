# Intensive Listening 2.1.0

2.1.0 为正式版本，提供 Windows x64 安装包、macOS Apple Silicon DMG，以及 Windows MDN 集控服务端。应用、安装器、macOS 包、遥测和 MCP 接口统一使用 2.1.0 版本号。

## MDN 组织管理

`MDN.Server.exe` 包含 Python 运行环境和管理后台，直接在专用工作目录运行，自动展开 `data` 并保持控制台。默认显示可用的局域网 IP，支持 HTTPS 与 `--disable_ssl` HTTP 模式，导出的 YAML 与当前协议一致。

客户端通过安装器导入、设置中选择 `il_mdn_config.yaml`，或填写服务器地址与组织共享注册凭证加入管理。设备使用安装 UUID，保存于用户数据目录；HTTPS 手动加入时确认服务器证书指纹。加入后锁定地址、凭证、HTTP／HTTPS 和证书配置，断线及重启后保持，保留状态、重试与管理员授权退出。主界面、设置和 OOBE 加粗显示组织名称。

WebSocket 管理在线状态并实时下发策略，断线自动重连，每分钟轮询补偿。后台支持全局／单机策略、部分／全部设置锁定、功能禁用、ASR 配置下发、设备与遥测查看、退出授权，鉴权失效返回登录页。客户端加密保存凭据与 ASR 配置，可隐藏 API 字段。独立 `lockManagement` 策略控制安装器对已有组织配置的替换。

组织遥测独立于阿里云开关，报告设备／版本、启动与在线状态、ASR 耗时／错误类型及崩溃诊断；音频、字幕和 API Key 不在上报范围内。

## 更新与精听包分发

后台可上传 EXE，自动计算 SHA-256 和字节数并签名，按全局或指定客户端下发。OTA 发送系统通知并自动下载、校验、安装和重启；冷更新发送新版本通知，在设置显示状态，用户点击后下载并安装。支持同版本覆盖更新，拒绝低版本，每次发布产生新的任务 ID，同一任务只自动安装一次。

Windows 更新助手兼容 PowerShell 5.1 与中文路径，确认启动后应用才退出。更新时保留用户数据和组织配置，需要时显示 UAC。失败结果与安装日志保存在 `data/updates`，可在设置重试。早期 2.0.1 预发布若无法启动更新助手，请先手动安装本版。

后台可上传 `.ilp`，向全部或多台指定设备分发，并查看接收回执。客户端自动下载、校验签名／大小／SHA-256 并导入；同 UUID 的高版本自动更新，保留播放、挖空、题目选择和答案显隐记录。同版／低版跳过，正在使用的课程延后导入；撤回停止继续接收，已导入课程保留。

## 播放、设置与部署

- 统一“导入精听包或音频”，默认筛选包括 `.ilp`；可在导入音频时自动建立工程。修复字幕虚拟列表越界崩溃和受管操作异常。
- 改进字幕列表滚动、跳转、冷状态与渲染。播放时连续 5 秒无操作进入冷状态，暂停时保持可操作，跳转后定位高亮句。
- 最近课程整行可点击，调整列表高度和间距；Windows 默认最大化，使用应用标题栏。
- 隐藏字幕和挖空文字共用 1–8 档模糊滑块，默认最大，最低保留少量模糊。
- 启动异步检查 GitHub Latest，10 秒超时，支持手动检查。独立关于页列出仓库、作者、许可、构建信息及 Ci2 借鉴说明。
- 改进设置页切换动画、安装欢迎引导和快捷键引导。播放／暂停未绑定时默认 PageUp，保留已有绑定。
- 安装器部署 `il_precfg.yaml`，OOBE 前应用指定设置预设；崩溃报告通过阿里云 ARMS 自定义事件报送。

## 下载与部署

- [Windows x64 安装包](https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.1.0/Intensive-Listening-2.1.0-win-x64-Setup.exe)
- [macOS Apple Silicon DMG](https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.1.0/Intensive-Listening-2.1.0-osx-arm64.dmg)
- [Windows MDN 服务端](https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.1.0/MDN.Server.exe)
- [MDN 部署、配置与管理指南](MDN.md)
- [安装预设与 OOBE 说明](DEPLOYMENT_PRESETS.md)

MDN 升级前备份并保留原 `data`，停止旧服务后替换 EXE。发布附件包括部署指南、预设示例和 SHA-256 校验文件。
