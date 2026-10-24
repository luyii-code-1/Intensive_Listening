# MDN 云服务管理契约

## 策略

客户端在 `X-IL-Capabilities` 追加 `cloudServiceV1`，与 `clientRoleV1`、`asrModeV1` 并列。`GET /api/policy` 向支持的客户端返回顶层 `cloudService` 和 `cloudCommands`。旧客户端省略这两个字段，继续获取原有角色、ASR、设置和更新策略；后台显示云管理需升级提示。

```json
{
  "cloudService": {
    "mode": "followLocal",
    "requireLogin": false,
    "lockAccount": false
  },
  "cloudCommands": []
}
```

`mode` 为 `followLocal`、`enabled`、`disabled`。跟随本机保留个人云连接开关；启用或禁用时锁定开关，仅施加运行时覆盖。全局默认如上；单机 `cloudService` 按子字段覆盖，省略的子字段继承全局。解除管理后恢复个人配置。离线沿用缓存策略。

`requireLogin` 表示开启云功能后，未登录或授权失效时显示浏览器授权入口，云操作等登录完成后执行。本地课程播放不受影响，也不自动打开浏览器。`lockAccount` 禁止终端主动退出或切换当前账号；未登录时允许首次授权。远程退出成功后解除当前绑定，允许重新授权，再锁定新账号。终端按稳定云账号 ID 判断绑定，不能仅比较显示名或邮箱。

显式 `disabled` 优先：关闭云网络活动和云功能；若 ASR 同时为 `forceCloud`，显示“云服务由组织关闭”，不回退传统 API。`requireLogin` 和 `lockAccount` 不隐式启用云连接；`forceCloud` 仍只选择 ASR 模式。学生端仍允许云账号登录和下载播放，制作禁用优先于 ASR 模式。关闭连接本身保留凭据，远程退出命令负责清理凭据。

策略使用现有 revision 和 `/api/policy/receipt` 回执；回执表示已应用策略，远程退出有独立终态回执。客户端应在设置、命令和服务调用处检查策略，保留个人配置。

## 账号与连接状态

`POST /api/cloud/status` 使用现有 MDN 设备 Bearer 凭据，必须声明 `cloudServiceV1`。在启动、登录状态或连接状态变化、策略应用、MDN 重连时发送。实时连接工作时不新增轮询，重连补报最新状态。

```json
{
  "sequence": 1,
  "policyRevision": 123,
  "enabled": true,
  "authentication": "signedIn",
  "connection": "connected",
  "account": {"id": "cloud-user-id", "email": "learner@example.com", "displayName": "学习者"},
  "sessionId": "4fe750af-8495-42ab-9057-4a7efef4f199",
  "errorCode": null
}
```

`sequence` 是终端持久化的单调递增正整数，按 MDN 注册周期计数。低序号或重复序号返回 `{ "accepted": true, "stale": true }`，不覆盖当前快照；新快照返回 stale false。设备重置注册后从 1 开始。发送失败只需保留最新快照并重试，不能在重试时改序号或旧快照内容。

`authentication` 为 `signedOut`、`authorizing`、`signedIn`、`reauthRequired`。仅 `signedIn` 携带 account 和 sessionId，其余两者均为 null。账号 ID 最长 128、邮箱最长 320、显示名最长 120，均禁止控制字符。`connection` 为 `disabled`、`connecting`、`connected`、`offline`、`error`；enabled false 必须为 disabled。`errorCode` 为 null 或 `network`、`authorization`、`credentialStore`、`service`，不能发送完整异常或令牌。

`sessionId` 是终端生成并加密持久化的随机 UUID，代表一代本机云凭据。进程重启、access/refresh token 刷新保留该 ID；新授权登录必须换新 ID，清理凭据后置空。不得使用 token 或 token 摘要作为 sessionId。

服务端记录接收时间，以 DPAPI 加密保存账号快照。后台数据为终端最近报告值，MDN 离线时展示最后报告时间，不当作云端在线证明。报告仅包含最小账号身份和状态，不包含密码、浏览器授权码、云 token 或计费余额。

## 远程退出

管理员 `POST /admin/cloud/logout` 表单包含 `device_id`、`command_id`（管理表单中的 UUID）和既有 `form_token`。仅针对单台设备。请求成功返回 `{ "accepted": true, "command": { ... }, "nextCommandId": "下一次操作的 UUID" }`。网络重试保留原 command_id，成功后使用 nextCommandId。重复 command_id 返回同一命令；同 ID 用于另一设备返回 409。设备已有待执行命令时返回该命令，并记录此次请求 ID 的映射，避免连续点击产生多次退出。

需有该设备最近一次 signedIn 报告、sessionId 和云管理能力；缺少状态返回 409，不支持能力返回 409。离线设备可排队，命令绑定已报告的登录会话。命令与策略分别保存，通过现有 WebSocket policy 通知促使客户端拉取，不为命令修改设置策略或版本。

WebSocket 心跳回复增加 `cloudCommandId`，有待执行命令时为命令 ID，否则 null。新版客户端看到待执行命令 ID 时补拉策略，不以 revision 相同为由跳过。策略应用回调也要处理同 revision 下的命令变化，命令接收后的去重和执行回执不依赖设置版本递增。该机制用于恢复遗漏的即时通知，实时模式不新增轮询。

```json
{
  "id": "8a92a52c-ce1f-4407-97eb-4fd20cde037e",
  "type": "logout",
  "targetSessionId": "4fe750af-8495-42ab-9057-4a7efef4f199",
  "createdAt": 1792540800.0
}
```

仅待执行命令放入 `cloudCommands`。终端串行执行命令及登录状态变更：若 targetSessionId 与本机当前凭据代不一致，回执 skipped，不退出新账号；匹配时先中止授权回调并禁止旧代请求写回登录态，撤销当前云设备凭据并清理本机 access/refresh token、账号绑定及缓存，保留本地课程和个人设置。关闭云连接时仍要处理本机凭据清理。远程退出不等于取消已受理云 ASR，不发送用户主动取消请求。

本机退出完成后把结果加密持久化并上报，重启或断线重试不得再次退出后来登录的账号。重复同一命令只重发结果。MDN 退出和重置设备注册时清理其云快照与命令。

`POST /api/cloud/commands/{id}/receipt` 使用 MDN 设备凭据和 cloudServiceV1：

```json
{ "status": "completed", "revocation": "revoked", "errorCode": null }
```

status 为 `completed`、`skipped`、`failed`；revocation 为 `revoked`、`pending`、`notRequired`。completed 表示本机凭据已清理；撤销网络失败用 pending，errorCode 用 network 或 service，仍视作本机退出完成，后台明确显示待确认云端撤销。skipped 必须配 notRequired、sessionMismatch。failed 用 credentialStore 或 service，表示未能完成本机清理，管理员可重新下发新命令。云端撤销的后续处理由客户端现有凭据撤销机制负责，不能把 pending 展示成已撤销。允许 completed 的 revocation 从 pending 推进至 revoked，其余终态不回退。回执只接受命令归属设备。

后台 `/admin/status` 每设备追加 `cloud` 对象，包含 supported、policy、state、reportedAt、stale（超过 90 秒无 MDN 活动或设备离线）、command（最近命令及终态）。state 只包含上面受限字段；命令仅含 ID、类型、目标会话、创建/完成时间、状态、撤销结果和错误类别。

## ASR 计费衔接

新云 ASR 代理按原始音频整单计算 `ceil(时长分钟) × 0.5` 积分，客户端切片不增加整单费用。MDN 仅管理云开关、账号约束和 ASR 模式；账单及任务终态以 Cloud 服务端为准。旧云任务的固定 5 积分成功与 2 积分主动取消规则不适用于新代理。
