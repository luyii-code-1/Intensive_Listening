# MDN 客户端角色契约

## 字段与能力

策略顶层新增 `clientRole`，取值 `default` 或 `student`。新版客户端每次获取 `/api/policy` 时在 `X-IL-Capabilities` 中追加 `clientRoleV1`，服务端向该客户端返回有效角色；未声明能力时省略此字段，仍通过已有 `disabledFeatures` 下发功能限制。

全局默认值为 `default`。单机省略字段时继承全局，显式 `default` 可覆盖全局学生端设置。服务端先合并全局与单机策略，再根据有效角色追加功能限制，不将追加结果写回管理员原始策略。

学生端在 `disabledFeatures` 中追加 `authoring`、`asr`、`mcp`、`export`、`importProject`、`apiConfiguration`。管理员原有禁用项保留；播放、音频导入和精听包导入继续遵循原有权限。学生端不能通过单机 `disabledFeatures: []` 绕过角色限制。

```json
{
  "revision": 123,
  "clientRole": "student",
  "settings": {},
  "lockedSettings": [],
  "disabledFeatures": ["authoring", "asr", "mcp", "export", "importProject", "apiConfiguration"]
}
```

## 客户端执行与回执

新版客户端应在命令、服务、MCP、云制作任务入口执行功能检查，并锁定制作相关设置的修改、导入和导出。设置值仅作为受管覆盖，保留个人值。策略生效后阻止新制作；在途云 ASR 不应因角色变化自动发送用户主动取消请求，其结果与费用以服务端终态为准。角色恢复默认只解除角色产生的限制，独立设置锁与功能禁用仍然有效。

学生端限制应覆盖导航、制作命令、快捷键及制作设置。云账号登录和已审阅课程的下载播放仍可使用；学生端的制作禁用优先于 ASR 模式选择。云连接默认关闭，开启云连接与是否允许制作分开处理。

沿用 `/api/policy/receipt` 的 `{ "revision": 123 }` 回执，客户端应在策略校验、缓存和应用后发送。管理后台显示已接收版本、有效角色，以及客户端是否声明完整角色支持。旧版通过功能禁用兼容，完整设置锁定需要升级客户端。

离线继续使用已保存的有效角色和策略；恢复连接后拉取最新版本。撤销角色或退出组织后恢复个人设置，角色不写入个人设置文件。传输、缓存加密和凭据存储依赖各平台现有实现；Windows 与 macOS 的实际链路由 Desktop 对话分别确认和验收。

## ASR 模式

顶层 `asrMode` 使用 `followLocal`、`forceApi`、`forceCloud`，单机省略时继承全局，默认 `followLocal`。`forceApi` 指传统 ASR API，具体配置沿用现有设置策略；`forceCloud` 指 IL Cloud 服务。现有 `settings.asrProvider: cloud/local` 表示原有 ASR 引擎选择，不能承载这三个含义。

模式强制时客户端锁定本机切换；撤销后恢复个人选择，离线保留缓存的有效强制模式。客户端以 `X-IL-Capabilities: asrModeV1` 声明支持后，服务端返回该字段。对旧客户端省略此字段，后台提示需升级以执行强制模式；沿用原有 API 设置和功能策略，不用旧 `asrProvider` 模拟新模式。角色与 ASR 模式独立继承，可在同一策略中指定。

回执继续使用当前策略版本。管理后台实时状态返回 `client_role`、`role_supported`、`role_warning`，以及 `asr_mode`、`asr_supported`、`asr_warning`，其中 supported 表示最近一次策略请求声明对应能力，不能代表功能已在设备实测。

ASR 计费由 Cloud 服务端处理，MDN 仅管理模式与功能策略。成功 5 积分；受理前取消免费，受理后用户主动取消 2 积分；服务端失败免费。断网或客户端超时不自动取消，完成与取消竞态由服务端按终态结算一次。
