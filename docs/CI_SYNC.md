# 开发分支同步 CI

在 Actions 中运行 **Publish 2.0-dev snapshot to main**。该工作流以现有 `main` 为父提交，把 `2.0-dev` 的完整文件树写入专用分支 `automation/sync-2.0-dev-main`，创建或更新其同步 PR，等待必需的 `build` 检查及合入条件，再合入并核对文件树。

同步保留 `main` 历史，快照包含开发分支中的文件删除、二进制内容和执行权限。重复运行复用同步分支与尚未合入的 PR；两端文件树相同时直接完成。该分支由同步工作流维护。

## 鉴权配置

工作流支持以下两种凭证。配置 GitHub App 时优先使用 App。

### GitHub App

1. 创建并安装专用 GitHub App，仅授予本仓库访问权限。
2. Repository permissions：`Contents`、`Pull requests`、`Workflows` 为 Read and write；`Checks`、`Commit statuses`、`Actions` 为 Read-only，Metadata 随 App 自动授予。
3. 在仓库 Settings → Secrets and variables → Actions → Variables 中设置 `IL_SYNC_APP_CLIENT_ID` 为 App 的 Client ID。
4. 在 Secrets 中设置 `IL_SYNC_APP_PRIVATE_KEY` 为 App 私钥 PEM 内容。

工作流使用 `actions/create-github-app-token@v3` 获取仅用于当前仓库的短期安装 token，任务结束时撤销。配置方法见 [GitHub 官方说明](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/making-authenticated-api-requests-with-a-github-app-in-a-github-actions-workflow)。

### 专用 PAT

在仓库 Secrets 中设置 `IL_SYNC_TOKEN`。推荐仅授予本仓库访问的 fine-grained PAT，Repository permissions 与上面的 App 权限一致。若使用 classic PAT，需要 `repo` 和 `workflow` scope。设置有效期，到期前更新 Secret。

Token 所属账号或 App 必须有推送同步分支、创建 PR 和合入 PR 的权限。工作流遵循 `main` 的 PR、检查及审批规则。仓库默认 `GITHUB_TOKEN` 在创建 PR、触发后续检查和修改 workflow 文件方面受到限制，使用专用凭证以支持自动同步；参见 [GitHub token 行为](https://docs.github.com/en/actions/concepts/security/github_token)。

## 运行与结果

1. 确认开发内容已推送到 `2.0-dev`。
2. 手动运行同步工作流。日志及 Summary 显示来源提交、文件树、同步 PR 和最终 `main` 提交。
3. 默认等待检查与审批最多 30 分钟，整个任务最多 35 分钟。仓库若要求人工审批，审批完成且合入条件满足后继续。
4. 检查失败、超时、PR 被关闭或头提交变化时任务停止，保留 PR 供排查；修复后重新运行。等待期间 `main` 或 `2.0-dev` 提交变化时也会停止，需要重新捕获快照。
5. 合入后检查 `main` 与捕获的开发分支文件树，同时确认当前开发分支文件树仍一致；核对失败在日志中报告，重新运行可同步后续变化。

修改同步 CI 的首次部署，通过正常 PR 合入 `main` 后即可从默认分支运行。应用安装包与 Release 不随同步工作流重新发布。

## 本地测试

```powershell
python -m unittest discover -s .github/scripts -p test_sync_dev_to_main.py -v
```

测试使用本地临时 Git 仓库与模拟 GitHub 响应，覆盖完整快照、删除与二进制／执行权限保留、PR 创建与复用、检查失败、源分支变化、超时及一致时跳过。常规 PR 构建与专用同步工作流均执行这些测试；真实 GitHub 自动同步需配置专用凭证后运行验收。
