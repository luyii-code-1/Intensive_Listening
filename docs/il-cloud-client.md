# Intensive Listening 云服务客户端

Windows 与 macOS 共用客户端。云连接默认关闭，完整 OOBE 提供启用和浏览器登录，可跳过；设置导航入口为“云服务”。API 设置和 OOBE 转写配置可勾选“使用云服务提供的接口”，也可使用自备 ASR API。

## 登录与课程

沿用 PKCE S256、回环回调及 cloud.library/cloud.projects/cloud.asr 权限；设备名称由应用生成。旧授权需重新授权 cloud.asr。刷新令牌保存到 Windows 系统凭据或 macOS Keychain，账号绑定、凭据代和待重试撤销记录加密保存。关闭连接保留凭据及本地课程；退出清理本机账号，网络撤销失败保留 pending，恢复连接后重试。

播放页读取 `/api/v1/library/playable`，只显示服务端确认可播放的课程。云图标表示未下载，圆环显示传输进度，失败可重试，导入后可打开，云端更新由用户点击下载。同 UUID 的更新保留学习记录。下载价格来自 downloadCredits，工程传输价格来自 wallet；制作页“云端工程”负责工程传输与任务查看。

## ASR 整单代理

新版使用 `/api/v1/asr/proxy`，价格和限制从 config 读取。当前整单费用为 `ceil(总采样数 / 960000) × 0.5` 积分，即原始音频分钟数向上取整，切片不增加费用。创建时冻结，全片供应商成功后结算；失败、未完成时取消或服务端超时释放冻结，不另收取消费。供应商成功后客户端解析失败不改变已完成结算。

1. 客户端切为 16kHz 单声道 PCM16 WAV，从实际 data 块计算采样数、哈希和大小。
2. 保存清单和幂等键，创建批次；响应丢失复用原键恢复。
3. 按 config 限制上传全部分片，最多 2 并发，同片重试复用批次与索引。
4. 全部上传后，每 2 秒查询整单，成功后读取各片原始供应商 JSON 和 HTTP 状态。
5. 沿用客户端阿里云解析器，按实际采样数累计偏移并合并 SRT。

断网、退出应用、MDN 限制和远程退出只停止本地等待。任务中心的主动取消才调用取消接口，终态和 chargedCredits 以服务端为准。重试继续原整单；终态失败后用户显式重试新建整单。旧固定单价任务不会隐式转成新批次，先确认云端原任务状态再新建任务。

## MDN

客户端声明 clientRoleV1、asrModeV1、cloudServiceV1。学生端保留登录、课程下载与播放，制作和 ASR 受限。强制 ASR 模式锁定本地选择；组织关闭云连接时不回退传统 API。

MDN 可强制开关、要求浏览器登录、查看最小账号和连接状态、锁定账号、远程退出。账号以稳定 ID 绑定；sessionId 是加密持久化的随机凭据代，新授权更换，token 刷新与重启保留。旧命令重放不会退出新会话。

状态 sequence 单调持久化，失败重试复用原快照。命令独立于设置 revision；同 revision 命令照常执行，心跳 cloudCommandId 可补拉漏通知。退出回执持久化，撤销结果可从 pending 推进至 revoked。离线沿用有效策略，解除后恢复个人值。

完整协议见 [MDN 云服务契约](mdn-cloud-service-contract.md)。Windows 使用 DPAPI，macOS 使用 Keychain 随机密钥加密缓存；macOS 实机链路需单独验收。

## 设置布局

单项和展开组统一采用 FluentAvalonia SettingsExpander。参考 ClassIsland 2 的[通用设置页](https://github.com/ClassIsland/ClassIsland/blob/2.0/ClassIsland/Views/SettingPages/GeneralSettingsPage.axaml)，共用图标、标题说明和右侧操作区，保留 Fluent 主题。借鉴声明见 THIRD_PARTY_NOTICES.md。
