# IL Cloud 客户端

云服务入口位于设置页的 **IL Cloud** 分类，适用于 Windows 和 macOS 主程序。

## 浏览器授权

客户端标识为 `il-desktop`，请求权限为 `cloud.library cloud.projects`。通过系统浏览器访问
`https://cloud.il.luyii.cn/connect/authorize`，采用 Authorization Code + PKCE S256。
应用先绑定 `127.0.0.1` 的系统分配端口，回调路径固定为 `/callback`；校验回调地址、随机 state 和重复字段。
浏览器等待五分钟后结束，也可在设置页取消。

授权码通过 `/connect/token` 换取令牌。访问令牌仅保存在内存；刷新令牌保存在 Windows 凭据管理器
或 macOS 钥匙串。刷新过程串行执行，收到轮换令牌后先原子写入系统凭据，再供请求使用。
`invalid_grant`、`invalid_token` 或资源接口 401 会清理当前凭据，要求重新授权。
退出登录调用 `/connect/revoke`；网络失败时仍清理本机凭据，并显示云端撤销尚未完成的状态。

`CloudApiTransport` 只向 IL Cloud 本身发送 Bearer 凭据并禁用自动重定向。
文件统一由云服务中转；客户端拒绝外域文件地址和重定向。

## 资源接入

设置页显示账号、共享容量、制作席位、云端文件、制作工程及最近制作任务。制作工程通过
`CourseProjectStore.ExportZipToFileAsync` / `ImportZipAsync` 互传；精听包通过
`IlpImporter` / `IlpLibrary` 校验与导入。配额和积分由云服务核定：免费两个制作席位，
ILP 与工程共用 200 MiB；上传免费，每次下载授权 0.2 积分。

传输记录存入数据目录的 `cloud-transfers`，按账号、资源和格式区分操作。
下载失败保留授权 ID 与已接收片段，重试复用原幂等键和授权；按登录设备区分下载记录。
接收时校验授权中的字节数与 SHA-256，成功保存、导入后清理缓存。
授权过期时清理该记录并提示再次申请的积分费用。
工程上传复用同一版本的 ZIP 快照，避免重试生成不同请求内容。
任务列表仅在设置页可见且有排队或执行中的任务时每五秒刷新。

参考：[RFC 8252](https://www.rfc-editor.org/rfc/rfc8252)、[RFC 7636](https://www.rfc-editor.org/rfc/rfc7636)、
[Windows Credential Manager](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)、
[macOS Keychain](https://developer.apple.com/documentation/security/keychain-services)。
