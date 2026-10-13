# 安装引导与预设部署

Windows 安装器首次向注册表写入设备 UUID 时，在 data 中记录完整引导模式。覆盖安装记录欢迎模式：只显示欢迎页，点击“进入应用”结束，原有个人设置保留。已有设置但缺少协议同意记录时，欢迎页提供协议与隐私勾选。

清空 data 或个人设置文件无效时，客户端恢复完整 OOBE；取消引导后下次启动继续完整流程。设备 UUID 仍优先沿用注册表。每次安装的完成记录继续使用独立的安装周期 ID，完成后普通重启不会重复展示 OOBE。组织 OTA 的自动 OOBE 策略继续生效。

完整引导顺序为：欢迎、协议、基本设置、快捷按键、外观、云端转写、完成。已有可用 API 配置时跳过云端转写页。快捷键页支持翻页笔或键盘单键录入，与设置中的快捷键编辑器一致。播放/暂停没有绑定时默认补充 PageUp；已有绑定保留，PageUp 已被其他动作占用时保留其用途。

## il_precfg.yaml

将预设文件命名为 `il_precfg.yaml`，放到安装包同目录。安装程序启动时复制到 data；客户端在 OOBE 前读取并保存指定设置。示例在 [il_precfg.example.yaml](../assets/deployment/il_precfg.example.yaml)，复制部署时改为正式文件名。

```yaml
schema_version: 1
settings:
  themeMode: dark
  transcriptFontSize: 20
  fileAssociationEnabled: true
  createProjectOnAudioImport: true
  mcpEnabled: false
  playerKeyBindings:
    togglePlayback: PageUp
    nextCue: PageDown
```

`settings` 字段使用与 MDN 一致的 camelCase 名称和类型；允许预设 UI、播放、制作、快捷键和 ASR 服务地址等普通设置。`cloudApiKey`、协议同意、阿里云遥测同意、安装完成记录与组织注册凭证不接受预设。字段无效时整份预设不应用，显示提示并保留已有设置。文件最大 32 KiB。

只覆盖指定字段，其他个人设置保留。每份设置内容应用一次，应用记录位于 `data/installation/preset-applied.sha256`；内容变更后再次应用。MDN 策略覆盖运行时设置，预设保存为个人值，退出管理后恢复这些个人值。通过预设启用文件关联时，进入主界面后同步应用该设置。

## ARMS 崩溃事件

应用崩溃报告通过 PC SDK 的[自定义事件接口](https://www.alibabacloud.com/help/zh/arms/user-experience-monitoring/sdk-configuration-reference-1)报送，事件类型 `intensive_listening`，事件名 `crash_report`。扩展字段包含 `report_id`、`occurred_at`、`app_version`、`platform`、`architecture`、`process_id`、`component`、`exception_type`、`message`、`details`、`fatal`，有值时附带 `exit_code` 和 `install_uuid`。

消息和堆栈继续过滤凭据。只有用户允许遥测并且网络可达时才提交；失败后保持本地报告以便重试，成功交给 SDK 后记录提交状态。红色错误提示仍沿用其日志报送接口，组织遥测使用独立通道。实际云端到账与展示可在 ARMS 后台按事件名、版本和报告 ID 核对。
