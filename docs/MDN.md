# MDN 集控与更新

本文介绍 Intensive Listening MDN 集控服务端。更新任务填写目标安装包的完整数值版本，例如本次正式版 `2.1.2.3`；安装器和 `MDN.Server.exe` 从[对应 Release](https://github.com/luyii-code-1/Intensive_Listening/releases/tag/v2.1.2.3)下载。

客户端目前支持 Windows。MDN 服务端使用 FastAPI、SQLite 和内置管理页面，Windows 部署文件为 `MDN.Server.exe`。开发源文件位于 `mdn_server`。

## Windows 服务端部署

1. 将 `MDN.Server.exe` 放到专用工作目录并直接运行。程序保持控制台（conhost）运行，在**当前工作目录**建立 `data`；运行环境随 EXE 打包。
2. 首次运行在控制台填写组织名、客户端访问地址和管理员密码。默认使用可用的局域网 IP，例如 `https://192.168.1.10:8443/`；控制台同时列出其他可用 IP，也可以填写域名。
3. 程序自动生成 HTTPS 证书、注册凭证、签名密钥、SQLite 数据库，并展开管理页面，初始化后直接启动服务。
4. 按控制台显示的地址登录后台。自动证书首次访问可能显示浏览器证书提示；客户端使用注册 YAML 中的证书指纹验证该证书。
5. 下载 `il_mdn_config.yaml`，放到客户端安装包同目录后运行安装器。防火墙允许对应端口入站。

安装器检测到同目录 YAML 时，会读取现有组织的加密策略缓存。独立策略 `lockManagement` 默认关闭：未锁定或旧版未声明锁定时可直接替换组织，清理旧凭证、策略与待发送诊断；设备 UUID 保留。开启后保留现有组织配置。同一组织更新 IP、域名或共享注册凭证时，保留设备凭证和策略，并更新缓存中的服务器地址。新 YAML 无效时保留原配置。

加入管理后立即注册并读取策略，然后通过 WebSocket 建立实时连接。使用现有设备凭证鉴权；HTTPS 对应 WSS，HTTP 对应 WS，证书沿用注册时的指纹。客户端每 25 秒发送心跳，服务端 75 秒未收到消息时断开。断线以 1–30 秒退避重连，重连后立即读取最新策略；默认每 60 秒轮询作为补偿。YAML 省略 `poll_seconds` 时也使用 60 秒；显式值按 15–3600 秒范围处理。

已安装的客户端也可在“设置 → 组织管理”输入 `IP:端口` 或 HTTPS 地址、共享注册凭证，点击“连接并加入管理”。首次连接仅获取组织名、公钥和证书信息；用户与管理员核对 SHA-256 指纹并确认后，才通过固定指纹的连接发送凭证。配置写入 data，重启后自动连接；页面支持立即重试。注册失败会撤回本次配置，允许修改地址和凭证后重新连接。

尚未加入管理的客户端可以点击“选择 YAML 加入管理”，选择管理员提供的 `.yaml` 或 `.yml` 文件，确认组织、服务器和传输方式后注册。已有管理的客户端不提供此入口。选择的文件名可自定义，保存到 data 时统一为 `il_mdn_config.yaml`。

实时连接只传递策略变更通知，客户端立即通过已鉴权的策略接口拉取完整配置，并执行原有校验与加密保存。全局变更通知全部在线设备，单机变更只通知目标设备。禁用或重置注册时关闭该设备的实时连接。后台每 5 秒刷新在线状态；实时客户端断开后显示离线，旧客户端按最近 90 秒的 API 活动显示状态。会话失效时后台自动返回登录页。

退出管理需要管理员在设备页面的“更多操作 → 授权退出管理”批准；客户端点击退出并通过服务器检查后，撤销设备凭证、删除组织配置和缓存，并恢复个人设置。管理员可在退出前撤销授权。设备 UUID 沿用原值。

关闭控制台或 Ctrl+C 可停止服务；再次运行沿用同目录配置和数据。公开监听参数在 `data/server.json`，页面在 `data/web/templates`，TLS 文件在 `data/tls`，秘密配置在 DPAPI 加密的 `data/config.bin`。TLS 私钥也使用随机密码加密，密码保存在 DPAPI 配置中。

更换新版 EXE 后再次运行，会迁移数据库并更新展开的管理页面；旧页面备份到 `data/web/backup-*`。后台提供设备搜索与在线状态、全局和单机策略表单、设置锁定、功能权限、ASR 配置、更新任务与遥测查看。高级 JSON 编辑仍可使用。Windows 服务使用 Selector 事件循环处理 HTTPS 连接关闭。

`--data D:\MDN\data` 可指定数据目录；`--init` 只执行首次初始化。服务默认监听 `0.0.0.0`，可用任务计划程序以原账号、原工作目录开机启动。备份整个 data，并保留运行账号的 Windows 密钥；初始化不会覆盖已有配置。

开发电脑打包命令：`powershell -NoProfile -ExecutionPolicy Bypass -File mdn_server\build-windows.ps1`，生成 `mdn_server/dist/MDN.Server.exe`。

## YAML 文件格式

### HTTP 模式

默认使用 HTTPS。可信内网可运行 `MDN.Server.exe --disable_ssl`，或在现有 `data/server.json` 中加入 `"disable_ssl": true` 后重启。启动参数优先开启 HTTP，本次启动沿用配置中的端口（默认 8443）；控制台和后台导出的连接地址自动使用 HTTP。HTTP 模式无需读取 TLS 证书或私钥，管理员登录会使用适用于 HTTP 的会话 Cookie。

客户端在加入管理前可在“设置 → 组织管理”选择 HTTP 或 HTTPS。加入后服务器地址、注册凭证、传输方式与服务器信任均锁定，断线时保持锁定；页面保留状态、连接重试和管理员授权退出。需要改变连接配置时，先获得管理员授权退出，再重新加入。安装器导入配置继续遵循独立的 `lockManagement` 策略。

HTTP 模式导出的 YAML 包含 `disable_ssl: true`、`server_url: "http://IP:端口/"` 和空的 `server_certificate_sha256`。客户端明确选择 HTTP 后确认明文传输风险。注册凭证、策略、遥测与 API 配置的传输不加密；本地敏感缓存仍使用 DPAPI。组织更新允许 HTTP 或 HTTPS 下载，签名、大小和 SHA-256 校验始终执行。

```yaml
schema_version: 1
organization_name: "示例学校"
server_url: "https://mdn.example.edu:8443/"
enrollment_token: "后台生成的组织共享注册凭证"
update_public_key: "后台生成的 RSA 公钥，SPKI DER Base64"
poll_seconds: 60
server_certificate_sha256: "后台生成的服务器证书 SHA-256 指纹"
disable_ssl: false
```

安装器按 lockManagement 策略将 YAML 导入 `%LOCALAPPDATA%\Intensive Listening\data\il_mdn_config.yaml`。应用读取 data 中的文件。组织名、服务器地址和共享注册凭证必填；协议需与 disable_ssl 一致，省略时默认 HTTPS。更新功能需要公钥。自动证书通过 `server_certificate_sha256` 固定指纹验证，错误指纹会拒绝连接；省略该字段时使用系统证书信任。轮询间隔允许 15–3600 秒，每次网络请求超时 10 秒。

YAML 含共享注册凭证，分发给组织的安装环境。注册时生成独立设备凭证，服务端仅保存其 SHA-256；客户端使用 DPAPI 保存凭证和策略缓存，之后以设备凭证请求策略和发送诊断。相同 UUID 再次注册会被拒绝，以防覆盖已有设备凭证。凭据丢失时，管理员选择“重置设备注册”，客户端退出后删除 `data\mdn\state.bin` 再启动。

设备 UUID 保存在 `data\device-id.txt`，与注册表 `Software\Intensive Listening\InstallUuid`、MDN 注册和上报使用相同值。升级沿用设备 UUID；旧安装优先迁移已有 UUID。安装器更新注册表并同步 data，应用也同步 HKCU。OOBE 使用独立的 `data\installation-cycle.txt` 安装周期标记；首次 UUID 注册或清空 data 后展示完整引导，覆盖安装只显示欢迎页。预设与详细规则见 [安装部署配置](DEPLOYMENT_PRESETS.md)。

OOBE 顶部显示组织名。主窗口顶部显示组织名和设备 UUID 最后一节。服务不可达时继续应用本地缓存的策略，并自动重试。

## 管理策略

后台“配置与策略 → 客户端模式”可分配默认或学生端。学生端保留播放与课程导入，锁定制作、ASR、工程导入导出、MCP 及 API 配置；单机可继承全局或显式恢复默认。设备列表显示有效角色和旧版兼容提示。协议、离线及撤销规则见[客户端角色契约](mdn-client-role-contract.md)。

“ASR 模式”支持跟随本机、强制传统 API 和强制云服务。强制模式由支持该能力的客户端锁定本机切换，撤销后恢复个人选择；设备列表显示模式及旧版升级提示。云服务费用由 Cloud 服务端结算。

后台支持“全部客户端”和单机范围。下发 `settings` 的字段会自动锁定；`lockedSettings` 可只锁定字段，保留其个人值。`lockAllSettings` 锁定全部可受管的设置。首次协议同意与阿里云遥测同意由用户控制，组织遥测独立运行。

```json
{
  "settings": {
    "themeMode": "system",
    "createProjectOnAudioImport": true,
    "cloudBaseUrl": "https://asr.example.edu",
    "cloudEndpoint": "/asr",
    "cloudModel": "model",
    "cloudApiKey": "组织 API Key"
  },
  "lockedSettings": ["transcriptFontSize"],
  "disabledFeatures": ["mcp", "export"],
  "lockAllSettings": false,
  "hideAsrConfiguration": true,
  "automaticOobe": true
}
```

支持的设置名由 `mdn_server/settings-schema.json` 定义，并显示在后台。客户端当前的识别引擎仍为云端英文 ASR；本地模型字段用于保存已有配置。

单机 `settings` 按字段覆盖全局。单机明确写入的布尔值与列表覆盖全局；省略字段继承全局。选择“恢复全局策略”可移除单机覆盖。“禁用注册”阻止该设备继续访问 API；需要禁用应用功能时使用 `disabledFeatures`，客户端会接收并执行。

功能名称：

| 名称 | 功能 |
|---|---|
| playback | 播放与跳转 |
| importAudio / importIlp | 导入音频 / 精听包 |
| authoring | 制作界面与项目操作 |
| asr | 转写任务，策略生效时取消在途任务 |
| mcp | 智能体接管服务 |
| export / importProject | 课程导出 / 工程导入 |
| dataMaintenance | 清理缓存与日志 |
| apiConfiguration | 修改、导入与导出 ASR 配置 |
| updates | 组织更新安装 |

受管设置与功能淡化，并显示黄色组织管理说明；实际调用也检查策略。组织配置作为运行时覆盖，个人设置仍保留。策略撤销后恢复个人值。`hideAsrConfiguration` 隐藏客户端 API 字段并阻止配置导出；密钥和配置缓存始终使用 DPAPI 加密。隐藏配置的 ASR 缓存标识只保存配置摘要；日志和错误提示过滤受管凭据。后台 API Key 使用 `__KEEP_SECRET__` 表示保持已有值，页面不回显密钥。

## 遥测

组织遥测强制运行，与设置中的阿里云遥测开关相互独立。报告包含设备 UUID、版本、启动与在线状态、ASR 成功状态／耗时／错误类型、崩溃异常类型与调用帧；课程音频、字幕、错误消息原文和 API Key 不在报告范围内。

客户端的待发送事件保存在 DPAPI 加密队列中，保留最近 128 条；崩溃报告使用独立发送标记，组织上报不改变阿里云报告状态。服务端按事件 ID 去重，保留最近 100000 条事件，后台展示最近 100 条。策略变更与设备操作写入 SQLite audit 表。

## 更新

普通更新检查每次启动异步访问本仓库 GitHub `/releases/latest`，只读 Latest；10 秒超时显示失败。设置“更新”中显示结果，并提供手动检查和发布页面入口。

组织更新在后台选择全局或单机范围，填写版本、HTTP/HTTPS 安装包直链、SHA-256 和字节数。安装包使用本项目的 Windows 安装器。服务器以 RSA PKCS#1 v1.5 / SHA-256 签名如下 UTF-8 内容（换行分隔，无末尾换行）：

也可以使用“上传本地安装包”：选择 EXE、版本、冷更新或 OTA，上传后自动计算大小和 SHA-256、签名并下发。后台显示上传进度，最大 2 GiB。文件保存在 `data/packages`，下载接口使用独立设备凭证，并检查安装包是否仍下发到该设备。停止任务后拒绝继续下载；已保存文件保留在服务器上。客户端仅向当前组织服务器发送设备凭证，外部下载地址沿用其系统证书信任。

```text
id
mode
version
url
sha256
size
```

客户端使用 YAML 公钥验签，再下载并检查字节数和 SHA-256。下载地址应直接返回安装包，客户端不跟随重定向。下载总时限为 20 分钟，上限为 2 GiB。

- `cold`：后台下载校验完成后显示 Windows 通知。点击通知开始安装；应用内弹窗提供“更新／稍后”，并定位“设置 → 更新”，只有选择更新才安装。
- `ota`：后台下载校验。窗口打开时应用内倒计时 10 秒，随后保存制作草稿、退出并安装，成功后恢复窗口并跳过 OOBE。选择延后时，关闭窗口即保存并安装，完成后保持关闭。窗口已关闭时直接静默安装，完成后保持关闭。安装时由用户确认 UAC。

允许相同版本覆盖更新，低版本拒绝。每次发布产生新任务 ID；同一任务只自动尝试安装一次。UAC 取消或安装失败会上报失败并等待管理员在反馈表中重新下发。回执保留在本地加密队列，连接恢复后重试发送。更新反馈及历史支持查询与清理，清理保留当前下发任务。

更新助手保存在 data 中，使用带 BOM 的 UTF-8 脚本兼容 Windows PowerShell 5.1。主程序等待助手确认启动后再退出。助手持有安装包只读锁并再次校验，以“Intensive Listening更新”程序名称请求 UAC，并静默安装。安装器保留数据及组织配置，通过 `/MDNDATA` 沿用原用户的数据路径。

`data/updates/install-update.log` 记录助手启动、校验、安装器退出码及重启。安装器详细日志为 `data/updates/installer-<任务 ID>.log`。助手写入 `result.json`，应用重新启动后读取结果；UAC、通知和安装器的实际交互由使用者验收。

前台 OTA 自动恢复窗口时跳过 OOBE；其他更新在需要展示欢迎页时显示“已成功更新到 vx.x.x”。首次协议仍需用户确认。即使更新后保持窗口关闭，后台反馈进程也会读取安装结果并上报状态。后台可停止尚未执行的下发任务。

## 精听包分发

后台“精听包分发”支持上传 `.ilp`，选择全部设备或多台指定设备。全局任务也会分发给以后注册的设备。服务端读取课程标题、UUID 和版本，校验音频／字幕哈希，生成带 RSA 签名的任务；单个包上限为 2 GiB。下载必须使用已注册设备凭证，并且任务仍在该设备的分发范围内。

客户端通过 WebSocket 通知和每分钟轮询获取任务，自动下载、验证签名／大小／SHA-256，并导入课程库。同 UUID 的高版本自动更新；同版或低版跳过。学习记录文件保持原样，正在播放的同一课程或打开的制作工程会延后导入，退出后再接收。导入新课程时保持当前播放状态。组织分发不依赖用户手动“导入精听包”功能权限。

播放进度、挖空记录、题目选择和答案显示状态保存在 `lesson_progress.json`。答题状态按题目 ID 恢复，旧记录缺少新字段时按空状态读取；高版本精听包沿用相同 UUID／题目 ID 时保留对应记录。

后台按设备显示等待接收、下载中、等待课程退出、导入完成、已有同版／高版或接收失败。回执发送失败会在后续连接中重试；已导入的任务在客户端重启后不会重复导入。撤回会停止该任务的继续下载与导入，已导入的本地课程会保留。客户端“设置 → 播放 → 组织精听包”显示接收状态并提供重试。

任务描述使用 camelCase 字段：`id`、`title`、`packageUuid`、`packageVersion`、`url`、`sha256`、`size`、`signature`。RSA PKCS#1 v1.5 / SHA-256 签名内容为 UTF-8，依次以换行连接 `ilp`、任务 ID、课程 UUID、课程版本、下载 URL、SHA-256、字节数，无末尾换行。标题以校验后的包清单为准。

“设置 → 播放 → 高速模糊强度”共用一个 1–8 档滑块，同时调整隐藏字幕和挖空文字，默认 8；最低档仍保留少量模糊。组织可通过 `settings.transcriptBlurIntensity` 指定并锁定该值，预设 YAML 使用相同字段。

客户端通过 `X-IL-Capabilities` 声明新设置支持；服务端向旧客户端返回策略时省略模糊强度字段，使旧客户端继续接收原有策略和更新任务。

## 开发验证

```powershell
python -m pip install -r mdn_server\requirements.txt httpx2
python -m unittest discover -s mdn_server -p test_server.py -v
```

测试覆盖注册鉴权与撤销、全局和单机策略继承、秘密保留与 DPAPI、更新签名、遥测字段范围与去重、安装后注册信息、管理员授权退出和旧数据库迁移。预发布另进行了真实 EXE 与 C# 客户端的 HTTPS 联调和中断 TLS 连接测试。安装器运行、UAC、完整 OTA 与界面交互需另外验收。
