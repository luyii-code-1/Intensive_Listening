# Desktop 版本与发布

当前基础版本为 `2.4.2`，在 `Directory.Build.props` 的 `DesktopBaseVersion` 中维护。

| 渠道 | 界面显示 | 标签 / 构建标识 |
| --- | --- | --- |
| 正式 Release | `v2.4.2` | 标签 `v2.4.2`，构建标识 `2.4.2` |
| 日常开发 | `v2.4.2 Insider Preview 1021 3` | `2.4.2-insider.20261021.3` |
| 人工 Pre Release | `v2.4.2 Beta 1021` | 标签 `v2.4.2-beta.20261021`，构建标识 `2.4.2-beta.20261021` |

日期使用北京时间。本地通过独占文件预留同日序号，CI 按当天 Insider 运行顺序编号，重跑复用原运行日期和编号。正式版及 Beta 不占 Insider 序号。

AppBuildInfo 的 Version 保留三段数值供协议、遥测和 MDN 比较；UpdateVersion 带渠道后缀用于 GitHub 更新检查；DisplayVersion 用于界面。Windows Assembly/FileVersion 使用 `2.4.2.0`，末位固定零；macOS plist 数值版本使用三段基础版本。MDN 同版本更新继续按任务 ID 去重。

## 本地入口

获得编译授权后，从 student_app 运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-csharp-windows.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-csharp-windows.ps1 -Channel beta
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-csharp-windows.ps1 -Channel release -ReleaseTag v2.4.2
```

完整 Windows/macOS 发布脚本同样准备版本。组合构建或直接运行 dotnet 前，先执行 `python scripts/build_version.py`，再设置 `IL_BUILD_VERSION_PREPARED=1` 让后续步骤复用标识。

生成文件在 artifacts/version，不提交。运行目录携带 build-info.json；安装器从运行目录读取基础版本、显示名和产物名称。

## CI 与官网

推送和 PR 构建为 Insider；手动构建默认 Insider，可选 Beta。正式 Release 事件校验标签与基础版本一致；预发布事件使用 Beta，标签日期须与构建日期一致。版本作业只生成一次元数据，各平台下载并复用。

正式 Release 构建及附件上传成功后，main 同步使用标签固定提交。官网只接受当前 Latest、非草稿、非预发布的三段标签；PWA 仍由自身 CI 管理。渠道标识不替代编译、发布或上线的用户授权。
