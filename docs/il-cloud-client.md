# Intensive Listening 云服务客户端

Windows 与 macOS 共用客户端。云连接默认关闭，完整 OOBE 提供启用和浏览器登录，可跳过；设置导航入口为“云服务”。API 设置和 OOBE 转写配置可勾选“使用云服务提供的接口”，也可使用自备 ASR API。

## 登录与课程

沿用 PKCE S256、回环回调及 cloud.library/cloud.projects/cloud.asr 权限；设备名称由应用生成。旧授权需重新授权 cloud.asr。刷新令牌保存到 Windows 系统凭据或 macOS Keychain，账号绑定、凭据代和待重试撤销记录加密保存。关闭连接保留凭据及本地课程；退出清理本机账号，网络撤销失败保留 pending，恢复连接后重试。

播放页读取 `/api/v1/library/playable`，只显示服务端确认可播放的课程。云图标表示未下载，圆环显示传输进度，失败可重试，导入后可打开，云端更新由用户点击下载。同 UUID 的更新保留学习记录。下载价格来自 downloadCredits，工程传输价格来自 wallet；制作页“云端工程”负责工程传输与任务查看。

## Desktop 云端转写

新建任务使用 `desktop-asr-v2`：客户端上传完整原始音频，服务器完成切片、阿里 API 调用、响应解析和 SRT 时间轴合并。价格与限制从 `/api/v1/asr/desktop/config` 读取，最长 30 分钟，最多 200 MiB。收费为 `ceil(服务器检测的总秒数 / 60) × 0.5` 积分；提交时冻结，字幕校验通过并成功完成后扣款，失败和主动取消释放全部冻结。

1. 上传前保存本地会话、完整音频哈希与请求键，调用 `/asr/desktop/uploads` 创建可恢复上传。
2. 每块最多 1 MiB，网络中断或确认丢失后查询服务器真实偏移，继续传输。传完调用 complete，由服务器检查实际格式、哈希和时长。
3. 以 `{audioAssetId,desktopV2:true}` 和独立请求键提交 `/asr/tasks`；接收响应丢失时复用原键。
4. 每 2 秒查询任务，显示上传、排队、切片、已完成分片数、按已识别音频时长计算的进度以及合并状态。暂时断网自动退避重连。
5. 成功后读取 `/asr/tasks/{id}/result` 的 SRT，写入本地任务与工程。

退出应用、MDN 限制和远程退出保留云端任务。应用重开后可在队列继续查询；主动取消以服务器终态为准，重开后的中断任务也须确认云端取消后才显示已取消。失败任务显式重试使用原任务 ID 和新的请求键，重新冻结总价；同模型复用已完成分片。服务器重启后从完整落盘的分片继续，原始音频和任务结果保存 7 天。

既有 `proxy-batch-v1` 队列任务继续使用代理客户端完成和结算，缓存身份与新任务区分。

## MDN

客户端声明 clientRoleV1、asrModeV1、cloudServiceV1。学生端保留登录、课程下载与播放，制作和 ASR 受限。强制 ASR 模式锁定本地选择；组织关闭云连接时不回退传统 API。

MDN 可强制开关、要求浏览器登录、查看最小账号和连接状态、锁定账号、远程退出。账号以稳定 ID 绑定；sessionId 是加密持久化的随机凭据代，新授权更换，token 刷新与重启保留。旧命令重放不会退出新会话。

状态 sequence 单调持久化，失败重试复用原快照。命令独立于设置 revision；同 revision 命令照常执行，心跳 cloudCommandId 可补拉漏通知。退出回执持久化，撤销结果可从 pending 推进至 revoked。离线沿用有效策略，解除后恢复个人值。

完整协议见 [MDN 云服务契约](mdn-cloud-service-contract.md)。Windows 使用 DPAPI，macOS 使用 Keychain 随机密钥加密缓存；macOS 实机链路需单独验收。

## 设置布局

单项和展开组统一采用 FluentAvalonia SettingsExpander。参考 ClassIsland 2 的[通用设置页](https://github.com/ClassIsland/ClassIsland/blob/2.0/ClassIsland/Views/SettingPages/GeneralSettingsPage.axaml)，共用图标、标题说明和右侧操作区，保留 Fluent 主题。借鉴声明见 THIRD_PARTY_NOTICES.md。
