# MDN 集控与更新

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

客户端可在“设置 → 组织管理”勾选“使用 HTTP（禁用 SSL）”。已加入管理的客户端可点击“应用连接方式并重连”，沿用设备 UUID、凭证和策略缓存。切回 HTTPS 时重新核对证书指纹；签名公钥必须与原组织一致。

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

- `cold`：显示“您的组织发布了新版本”系统通知；点击通知定位“设置 → 更新”。用户点击“更新”后才下载、校验和安装，页面显示进度阶段与失败状态。
- `ota`：显示“您的组织正在更新您的应用”系统通知，自动下载校验、保存制作草稿、退出应用、执行安装器并重启。系统需要管理员权限时显示 UAC。

允许相同版本的覆盖更新，低版本继续拒绝。每次后台发布产生新的任务 ID；通知按任务去重，同一任务只自动尝试安装一次，下载失败可自动重试。客户端重启后记录完成的任务，已完成任务不再次执行；同版本的新任务仍可执行。安装取消或失败后，可在“设置 → 更新”手动重试。

更新助手保存在 data 中，使用带 BOM 的 UTF-8 脚本兼容 Windows PowerShell 5.1，并显式按 UTF-8 读取计划。主程序等待助手确认启动后再退出；启动失败或超时会保留应用并提示重试。助手等待原应用退出，持有安装包只读锁并再次校验，发送安装阶段的系统通知，再调用安装器。静默更新只接受已识别的本项目 2.x 安装目录。安装器保留数据和已有组织配置；通过 `/MDNDATA` 沿用原用户的数据路径。

`data/updates/install-update.log` 记录助手启动、校验、安装器退出码及重启。安装器详细日志为 `data/updates/installer-<任务 ID>.log`。助手写入 `result.json`，应用重新启动后读取结果；UAC、通知和安装器的实际交互由使用者验收。

设置 `automaticOobe: true` 时，已经同意协议的组织更新会使用下发偏好并自动完成此次安装周期的 OOBE。首次协议仍需用户确认。UAC 取消或安装失败会写入结果、重新启动可用的应用，随后可在设置中重试安装；相同更新不会反复自动弹出 UAC。后台可停止尚未执行的更新下发。

## 开发验证

```powershell
python -m pip install -r mdn_server\requirements.txt httpx
python -m unittest discover -s mdn_server -p test_server.py -v
```

测试覆盖注册鉴权与撤销、全局和单机策略继承、秘密保留与 DPAPI、更新签名、遥测字段范围与去重、安装后注册信息、管理员授权退出和旧数据库迁移。预发布另进行了真实 EXE 与 C# 客户端的 HTTPS 联调和中断 TLS 连接测试。安装器运行、UAC、完整 OTA 与界面交互需另外验收。
