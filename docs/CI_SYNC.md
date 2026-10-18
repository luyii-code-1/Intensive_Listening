# 开发与发布分支

日常功能、修复和 PR 合入 `2.0-dev`。`main` 保存正式 Release 对应的代码。

## 发布来源

**Publish Release source to main** 在正式 Release 的 `C# Windows build` 流水线成功后运行。也可手动运行，必须指定已发布的正式 Release 标签。

工作流检查标签是当前 Latest 正式 Release，解析标签所指的固定提交。预发布、草稿和旧正式版不会推进 main。普通开发推送和 PR 检查只执行构建，不触发发布同步。

同步以现有 `main` 和 Release 来源提交为两个父提交，构造完整文件树快照，通过 `automation/sync-2.0-dev-main` 分支创建或更新 PR。必需的 `build` 检查及审批通过后，正常合入 main，并核对来源与文件树。

如果 `2.0-dev` 仍指向 Release 来源，就快进至合入后的 main；如果开发分支已有更新，则保留开发分支提交。检查期间标签或 main 变化会停止同步。同步不重新上传安装包或创建 Release。

## 凭据

可选择 GitHub App 或专用 PAT。配置了 `IL_SYNC_APP_CLIENT_ID` 时优先使用 App。

- App：仓库变量 `IL_SYNC_APP_CLIENT_ID`，Actions Secret `IL_SYNC_APP_PRIVATE_KEY`。
- PAT：Actions Secret `IL_SYNC_TOKEN`，使用时不设置 App 变量。

Fine-grained PAT 仅授权当前仓库：Contents、Pull requests、Workflows 为读写；Checks、Commit statuses、Actions 为只读。Token 所属账号或 App 需有推送同步分支、创建和合入 PR 的权限。工作流沿用 main 的保护规则。

PAT 在 GitHub 个人 Settings → Developer settings → Personal access tokens 中创建，生成后将实际值填入仓库 Settings → Secrets and variables → Actions → Secrets。

## 运行与恢复

1. 将待发布内容集成到 `2.0-dev`，从验收提交创建正式 Release 标签。
2. 正式 Release 发布并构建成功后，查看同步工作流 Summary 中的标签来源、文件树和 PR。
3. 检查失败或超时会保留 PR。修复后对同一正式 Release 重新运行；最长等待检查 30 分钟。
4. 本地继续开发前执行 `git fetch origin`，再用 `git merge --ff-only origin/2.0-dev` 更新本地分支。

新触发器进入默认分支后生效。首次启用时，先从 `2.0-dev` 手动运行固定正式标签的同步；同步成功后，后续正式 Release 构建可自动触发。示例：

```powershell
gh workflow run sync-dev-to-main.yml --ref 2.0-dev -f tag=v2.x.y
```

将示例标签替换为当时已发布的 Latest 正式 Release。

## 验证

```powershell
python -m unittest discover -s .github/scripts -p test_sync_dev_to_main.py -v
```

测试使用临时 Git 仓库和模拟 GitHub 响应，覆盖完整树、双父历史、权限与二进制保留、PR 检查、并发推进，以及固定 Release 来源、预发布拒绝和后续开发保留。
